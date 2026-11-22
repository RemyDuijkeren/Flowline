using System.ComponentModel;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using CliWrap;
using CliWrap.Buffered;
using Flowline.Core;
using Flowline.Core.Config;
using Flowline.Core.Console;
using Flowline.Core.Dataverse;
using Flowline.Core.Deploy;
using Flowline.Core.Environments;
using Flowline.Core.Solutions;
using Flowline.Core.Validation;
using Flowline.Core.Models;
using Flowline.Diagnostics;
using Flowline.Services;
using Flowline.Settings;
using Flowline.Utils;
using Spectre.Console;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class DeployCommand(CommandServices services, DataverseConnector dataverseConnector, IEnumerable<IPostDeployService> postDeployServices) : FlowlineCommand<DeployCommand.Settings>(services)
{
    public sealed class Settings : DataverseSettings
    {
        [CommandArgument(0, "<target>")]
        [Description("Target environment: prod, uat, test, or a URL")]
        public string Target { get; set; } = null!;

        [CommandOption("--path <zip>")]
        [Description("Import this pre-built solution zip instead of packing from source")]
        public string? Path { get; set; }

        [CommandOption("--skip-dtap-check")]
        [Description("Skip DTAP promotion checks")]
        [DefaultValue(false)]
        public bool SkipDtapCheck { get; set; } = false;

        [CommandOption("--skip-solution-check")]
        [Description("Skip the solution checker gate")]
        [DefaultValue(false)]
        public bool SkipSolutionCheck { get; set; } = false;

        // Deliberately not --skip-dependency-check: `pac solution import` already uses that name for a
        // narrower product-update check, and reusing it would read as the same thing.
        [CommandOption("--skip-component-check")]
        [Description("Skip the missing-component gate that checks the target before importing")]
        [DefaultValue(false)]
        public bool SkipComponentCheck { get; set; } = false;

        [CommandOption("--no-backup")]
        [Description("Skip the pre-deploy environment backup")]
        [DefaultValue(false)]
        public bool NoBackup { get; set; } = false;

        [CommandOption("--no-delete")]
        [Description("Report orphan components without deleting them")]
        [DefaultValue(false)]
        public bool NoDelete { get; set; } = false;

        [CommandOption("--binaries-from-build")]
        [Description("Pack plugin and web resource binaries from a fresh build instead of the committed copies; the rest of the solution stays as committed")]
        [DefaultValue(false)]
        public bool BinariesFromBuild { get; set; } = false;

        [CommandOption("--no-build")]
        [Description("With --binaries-from-build: skip 'dotnet build' and use the existing Release output")]
        [DefaultValue(false)]
        public bool NoBuild { get; set; } = false;

        [CommandOption("--dry-run")]
        [Description("Run every deploy pre-flight check and back up the target, without importing the solution")]
        [DefaultValue(false)]
        public bool DryRun { get; set; } = false;
    }

    // Extracted from ExecuteFlowlineAsync so the skip flags and — more importantly — the pre-import
    // ordering can be asserted directly. R13 requires the missing-component gate to run ahead of the
    // solution checker and the environment backup, and that guarantee is bought entirely by DI
    // registration order in Program.cs. Preserving the incoming order here is therefore load-bearing:
    // reordering the registrations, or sorting this list, silently breaks R13 with no compile error.
    internal static List<IPostDeployService> ResolveActiveServices(IEnumerable<IPostDeployService> services, Settings settings)
    {
        bool IsSkipped(IPostDeployService s) =>
            settings.SkipComponentCheck && s is MissingComponentCheckService ||
            settings.SkipSolutionCheck && s is SolutionCheckService ||
            settings.NoBackup && s is BackupService;

        return services.Where(s => !IsSkipped(s)).ToList();
    }

    // FIX A: best-effort, matching MissingComponentReport.ClearReport's own tolerance for IO failure —
    // clearing a stale report is a courtesy, never a reason to fail a deploy that's already skipping the gate.
    internal static void ClearComponentCheckReportIfSkipped(bool skipComponentCheck, string packagePath, string targetUrl)
    {
        if (skipComponentCheck)
            MissingComponentReport.ClearReport(packagePath, targetUrl);
    }

    // "drift" is this command's local force hazard (skip drift validation) — distinct from the
    // unrelated `flowline drift` CLI command, which reports drift for any environment read-only.
    internal static readonly string[] ValidSpecifiers = ["drift", "first-import", "delete-orphans", "all"];
    protected override string[] ValidForceSpecifiers => ValidSpecifiers;

    // U3: `--path` set and no `.flowline` found walking up from cwd. Settings-aware (KTD1, see base
    // class) since standalone is gated on a flag, not a fixed command-wide property. Split into a pure
    // helper (ResolveStandalone) taking startDir explicitly, since this runs during ExecuteAsync's
    // project-root resolution (FlowlineCommand.cs) — before RootFolder itself is assigned — so it can't
    // read RootFolder the way GenerateCommand's own standalone predicate does.
    protected override bool IsStandalone(Settings settings) => ResolveStandalone(settings.Path, Directory.GetCurrentDirectory());

    internal static bool ResolveStandalone(string? path, string startDir) =>
        !string.IsNullOrWhiteSpace(path) && FindFlowlineProjectRoot(startDir) is null;

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var usingExplicitArtifact = !string.IsNullOrWhiteSpace(settings.Path);
        SolutionPackService.ValidateBuildFlags(usingExplicitArtifact, settings.BinariesFromBuild, settings.NoBuild);
        var standalone = IsStandalone(settings);
        var target = ResolveTarget(settings.Target, Config, standalone);
        var targetUrl = target.Url;

        // U3/KTD2: standalone only, and hoisted this high only because standalone builds its
        // ProjectSolution from this manifest instead of Config, and `sln` is consumed below (target
        // validation, artifact path) before the `usingExplicitArtifact` branch that used to own this read.
        // Deliberately NOT hoisted for --path inside a project (R2: that route must behave exactly as it
        // does today). Hoisting it there too would move a corrupt-zip failure ahead of ValidateTargetAsync,
        // so the environment check line and its round-trip would stop happening first — a visible reordering
        // of project-mode output, which the Goal Capsule puts out of scope. That route keeps reading the
        // manifest at its original point below; each route still parses the zip exactly once.
        var artifactManifest = standalone
            ? ReadArtifactSolutionManifest(settings.Path!)
            : ((string Version, bool Managed, string? UniqueName)?)null;

        var sln = standalone
            ? ResolveStandaloneSolution(settings.Path!, artifactManifest!.Value)
            : Config.Solution ?? throw new FlowlineException(ExitCode.ConfigInvalid, "No solution configured — run 'clone' first.");

        // R14: standalone only — a CI job in a scratch folder has no other way to tell which mode it
        // got or where identity came from. Project mode says nothing new here: identity has always come
        // from config, and the Goal Capsule scopes this plan out of changing project-mode output.
        if (standalone)
            Console.Info(BuildStandaloneIdentityNote(Path.GetFileName(settings.Path!)));

        var slnFolder = RootFolder;

        // --path supplies a prebuilt artifact packed elsewhere, so nothing on that route reads the Dataverse
        // solution folder — not the git-clean scope, the DTAP gate's local version, the drift check, or the pack.
        // Resolve it only when a route needs it, so `deploy --path <zip>` still works in a repo without a
        // solution file (a CI checkout carrying only the artifact), the way it did before discovery replaced
        // the on-disk cdsproj check. On the packed route the layout is loaded once and threaded through
        // every read below, so one deploy never parses the solution file twice and acts on two answers.
        var layout = usingExplicitArtifact
            ? null
            : await SolutionFileLayout.LoadAsync(slnFolder, cancellationToken);
        var dataverseSolutionFolder = layout?.DataverseSolutionFolder;

        // --path supplies an artifact that wasn't necessarily packed from the current local tree, so neither
        // check is meaningful there: git-clean and drift both assume packagePath is derived from the
        // Dataverse solution folder's src/.
        IReadOnlyList<string> deploymentInputPaths = [];
        IReadOnlyList<SlotCopy> buildOutput = [];
        if (!usingExplicitArtifact)
        {
            deploymentInputPaths = SolutionPackService.GetPackInputPaths(layout!, dataverseSolutionFolder!, includeProjectSources: settings.BinariesFromBuild); // non-null on the packed route
            await SolutionPackService.ValidateGitCleanAsync(deploymentInputPaths, RootFolder, _capture, "deploy", cancellationToken);

            // Offline and cheap, so it fails before any target check or connection. The pack step stages again:
            // ponytail: one extra copy of the tracked files, cheaper than holding a temp folder open across the
            // whole deploy; hold it instead if solution folders grow large enough for the copy to show.
            // Under --binaries-from-build the build and slot match run here too, for the same reason.
            if (settings.BinariesFromBuild)
            {
                buildOutput = await SolutionPackService.BuildForPackAsync(Console, _capture, layout!, sln.UniqueName, sln.PluginPackageMode, settings.NoBuild, RootFolder, cancellationToken);
            }
            else
            {
                var precheck = await SolutionPackService.StageTrackedSolutionAsync(dataverseSolutionFolder!, RootFolder, _capture, cancellationToken);
                try { SolutionPackService.EnsureSlotsHaveBinaries(precheck); }
                finally { SolutionPackService.DeleteStagedFolder(precheck, Logger); }
            }
        }

        var (targetEnv, existingSolutionInTarget, resolvedProfile) = await ValidateTargetAsync(target, sln, settings, cancellationToken);

        // Resolve the DTAP gate's version cheaply (artifact manifest or local Solution.xml) so the gate keeps
        // failing fast before any expensive work — packing itself is deferred past the gate below.
        string gateVersion;
        string? currentCommitSha = null;

        if (usingExplicitArtifact)
        {
            // Standalone already read this above (it needed the unique name to build `sln` at all);
            // --path inside a project reads it here, exactly where it always did. Either way the zip is
            // parsed once per run, so one deploy can never act on two answers.
            var (artifactVersion, artifactManaged, artifactUniqueName) = artifactManifest ?? ReadArtifactSolutionManifest(settings.Path!);
            ValidateArtifactSolutionName(artifactUniqueName, sln.UniqueName, settings.Path!, standalone);
            // KTD2/KTD3: standalone's sln.IncludeManaged is itself derived from this same artifact
            // manifest above (ResolveStandaloneSolution) — comparing it back here would always pass, so
            // skip it there. Project mode's --path still compares against a genuinely independent source
            // (config), so the check stays live for that route.
            if (!standalone)
                ValidateArtifactManagedFlag(artifactManaged, sln.IncludeManaged);
            gateVersion = artifactVersion;
        }
        else
        {
            currentCommitSha = await GitUtils.GetLastCommitShaForPathAsync(deploymentInputPaths, RootFolder, _capture, cancellationToken);
            gateVersion = ReadLocalSolutionVersion(dataverseSolutionFolder!); // non-null on the packed route
        }

        await ValidateDtapGateAsync(sln, gateVersion, targetUrl, settings, cancellationToken);

        // ValidateDtapGateAsync's predecessor resolution (U4) can switch PAC's active profile away
        // from the target when predecessor and target use different auth profiles — re-guard the
        // target here so every pac.exe call below (import, etc.) runs under the right profile again.
        await ProfileResolutionService.ResolveAsync(targetUrl, cancellationToken);

        // R8: placed after the DTAP gate, not right after ValidateTargetAsync — a `dev` target is already
        // rejected by the gate's DevBlock outcome above, so a first-import confirmation would otherwise fire
        // on a deploy that's about to be blocked moments later anyway.
        if (!existingSolutionInTarget)
        {
            if (settings.DryRun)
            {
                Console.Info(BuildFirstImportDryRunNote(sln.UniqueName, targetEnv.DisplayName!, sln.IncludeManaged));
            }
            else if (!await AnsiConsole.Console.ConfirmAsync(BuildFirstImportPrompt(sln.UniqueName, targetEnv.DisplayName!, sln.IncludeManaged), false, RuntimeOptions, "first-import", cancellationToken))
            {
                Console.Info("Deploy cancelled. Re-run with --force first-import to skip this confirmation.");
                return (int)ResolveFirstImportDeclinedExitCode();
            }
        }

        // --path supplies a prebuilt artifact, so the local drift guard has nothing to compare it with; with
        // build output in the zip, the difference it reports is the point.
        if (!usingExplicitArtifact && !settings.BinariesFromBuild)
            await SolutionPackService.ValidateDriftAsync(Console, RuntimeOptions.HasForce("drift"), sln.UniqueName, layout!, dataverseSolutionFolder!, "deploy", cancellationToken);

        // Managed import only removes components no longer in the solution when Dataverse runs it as an
        // Upgrade (pac's --stage-and-upgrade) — plain import ("Update" semantics) never deletes anything,
        // managed or not. Upgrade also requires a prior version already installed, so it's only valid once
        // this solution exists in the target — a first-time managed install stays a plain import, same as
        // before. When Upgrade doesn't apply (unmanaged, or no prior version), orphan cleanup still runs to
        // fill that gap, but forced into report-only mode for managed — OrphanCleanupService's Delete/
        // RemoveSolutionComponent calls target components owned by the managed solution's own layer, which
        // Dataverse rejects outside its own upgrade/uninstall path, so mutating there only produces failed-
        // cleanup noise. The report itself stays valuable: a preview of what Upgrade will remove, or (when
        // no prior version) a signal that cleanup still needs a later managed Upgrade deploy to catch up.
        var useStageAndUpgrade = sln.IncludeManaged && existingSolutionInTarget;
        var runMode = ResolveRunMode(settings.DryRun, settings.NoDelete, sln.IncludeManaged);

        // pac's --publish-changes runs PublishAllXmlRequest, not the solution-scoped PublishXmlRequest —
        // it republishes every pending customization in the ENTIRE target environment, not just this
        // solution's components:
        //   - https://learn.microsoft.com/power-platform/alm/performance-recommendations
        //     ("doesn't apply only to the selected solution... publishes all pending changes across the
        //     entire environment" — same doc says skip it for managed, since it "slows down the deployment")
        //   - https://learn.microsoft.com/power-platform/developer/cli/reference/solution#pac-solution-publish
        //     (`pac solution publish` itself is documented as "Publishes all customizations")
        //   - https://learn.microsoft.com/dotnet/api/microsoft.crm.sdk.messages.publishallxmlrequest
        //     (the underlying SDK message — "publish all changes to solution components", no
        //     solution-scoping parameter exists on it at all)
        // Managed solutions always import already published, so the flag would be pure overhead there —
        // never pass it for managed. Unmanaged imports can leave UI-affecting components (forms, views,
        // ribbons, sitemaps, web resources) in an unpublished state until something publishes them, so the
        // flag is passed there and its environment-wide cost is accepted. That cost is not always trivial:
        // ResolveDtapGate gates on version ordering only, never on managed/unmanaged, so an unmanaged deploy
        // to a shared Test/UAT target is a supported path — and there the org-wide publish sweeps up every
        // other pending customization in that environment, not just this solution's.
        // The scoped alternative (not taken): drop --publish-changes and issue a solution-scoped
        // PublishXmlRequest over this solution's components through the already-connected IOrganizationService,
        // which publishes only what was imported. Costs building the component list from the packed solution;
        // revisit if org-wide publish time on shared Test/UAT becomes a real complaint.
        var publishChanges = !sln.IncludeManaged;
        Logger.LogInformation("target={TargetUrl} solution={SolutionName} mode={RunMode} managed={Managed} stageAndUpgrade={StageAndUpgrade} publishChanges={PublishChanges}",
            targetUrl, sln.UniqueName, runMode, sln.IncludeManaged, useStageAndUpgrade, publishChanges);

        var (service, _) = await ConnectToDataverseAsync(dataverseConnector, targetUrl, cancellationToken, resolvedProfile);

        // A <Solution>_Upgrade holding solution left by an earlier staged upgrade blocks every further upgrade,
        // and pac reports it only as a failed import. Checked before pack, backup and solution checker so it
        // fails in seconds, under --dry-run too. Only the managed upgrade path can meet one: a holding solution
        // is always managed, and unmanaged over managed is already refused in ValidateTargetAsync.
        if (useStageAndUpgrade)
        {
            var holdingVersion = await new SolutionReader().GetInstalledVersionAsync(service, HoldingSolutionName(sln.UniqueName), cancellationToken);
            if (BuildPendingUpgradeMessage(sln.UniqueName, holdingVersion, gateVersion, targetEnv.DisplayName!, targetUrl) is { } pendingUpgrade)
                throw new FlowlineException(ExitCode.ValidationFailed, pendingUpgrade);
        }

        string packagePath;
        if (usingExplicitArtifact)
        {
            packagePath = settings.Path!;
        }
        else
        {
            Logger.LogInformation("Packing: {SolutionName}", sln.UniqueName);
            packagePath = SolutionPackService.ResolveZipPath(slnFolder, output: null, sln.UniqueName, gateVersion, sln.IncludeManaged);
            var staged = await SolutionPackService.StageTrackedSolutionAsync(dataverseSolutionFolder!, RootFolder, _capture, cancellationToken); // non-null on the packed route
            try
            {
                SolutionPackService.OverlayBuildOutput(staged, buildOutput);
                await SolutionPackService.PackAsync(Console, _capture, sln.UniqueName, staged, packagePath, sln.IncludeManaged, cancellationToken);
            }
            finally { SolutionPackService.DeleteStagedFolder(staged, Logger); }
        }

        // R5: fires regardless of whether the subsequent import succeeds — the packed zip is already
        // valid and potentially useful (manual retry, inspection) once it's resolved, independent of
        // origin (fresh pack or --path) or import outcome. U3: never fires under --dry-run —
        // this signal hands a packed artifact off to a later pipeline stage for real promotion, and
        // --dry-run never produces one.
        if (!settings.DryRun)
            SolutionPackService.PublishArtifactForCi(Logger, packagePath, sln.UniqueName, gateVersion);

        // Always unpack the zip actually being imported — whether freshly packed or supplied via --path — so post-deploy services evaluate real imported content, never an assumed
        // local package source that may not match (e.g. a --path artifact built from a different commit).
        var tmpUnpackDir = Directory.CreateTempSubdirectory("flowline-deploy-").FullName;
        try
        {
            await PacUtils.UnpackSolutionAsync(packagePath, tmpUnpackDir, sln.IncludeManaged, _capture, cancellationToken);

            var solutionInfo = new DeploySolutionInfo(sln.UniqueName, targetEnv.EnvironmentUrl!, sln.IncludeManaged, existingSolutionInTarget);
            // KTD2: the checkout's own src/ — distinct from tmpUnpackDir above (the actually-imported
            // content, whether freshly packed or --path). Null on the --path route, which
            // leaves dataverseSolutionFolder unresolved (no solution file needed there) — the provenance
            // lookup then reads every entry as Undetermined rather than guessing a path.
            var checkoutSolutionSrcRoot = dataverseSolutionFolder != null ? Path.Combine(dataverseSolutionFolder, "src") : null;
            var postDeployContext = new PostDeployContext(service, solutionInfo, runMode, packagePath, tmpUnpackDir, RuntimeOptions.HasForce("delete-orphans"), checkoutSolutionSrcRoot);

            var activeServices = ResolveActiveServices(postDeployServices, settings);

            // FIX A: a skip means "no current verdict" — a report left by an earlier blocked run against
            // this target no longer describes anything real, so skipping must clear it rather than let a
            // stale block survive (e.g. the missing app gets installed, then the next deploy runs with
            // --skip-component-check and would otherwise still show the old report as if it were current).
            ClearComponentCheckReportIfSkipped(settings.SkipComponentCheck, packagePath, targetEnv.EnvironmentUrl!);

            // R11: a disclaimer about the orphan verdicts the loop below is about to print — never touches
            // checkoutSolutionSrcRoot or any Provenance value the engine computed (KTD2: the engine's own
            // report stays commit-agnostic). Silent on the trusted case, per tone-of-voice's "no preamble" —
            // only the degraded routes have anything to say.
            await PrintProvenanceTrustNoteAsync(usingExplicitArtifact, slnFolder, currentCommitSha, gateVersion, cancellationToken);

            foreach (var postDeployService in activeServices)
                await postDeployService.RunPreImportAsync(postDeployContext, cancellationToken);

            // U3/KTD5: every check above (DTAP, git-clean, local drift, packing, solution checker, orphan
            // preview, backup) runs identically for dry-run and a real deploy — this is the one branch
            // where they diverge. --dry-run stops here: no import, no post-import cleanup.
            if (settings.DryRun)
            {
                Console.Done(BuildDryRunCompleteMessage(sln.UniqueName, targetEnv.DisplayName!));
                return 0;
            }

            Logger.LogInformation("Importing to: {TargetUrl}", targetUrl);
            await ImportSolutionAsync(packagePath, targetEnv, sln.UniqueName, useStageAndUpgrade, publishChanges, cancellationToken);

            var postImportOutcomes = new List<PostDeployOutcome>();
            foreach (var postDeployService in activeServices)
                postImportOutcomes.Add(await postDeployService.RunPostImportAsync(postDeployContext, cancellationToken));
            var postImportFindings = postImportOutcomes.Sum(o => o.Findings);
            Logger.LogInformation("Post-deploy findings: {Findings}", postImportFindings);

            // KTD5: source-neutral. More than one post-import service can now report, so this sums every
            // outcome and names no cause — each service already printed its own findings and its own fix
            // above (orphan cleanup its maker-portal action, the package assembly check its remedy).
            // Claiming every finding is an orphan was true while orphan cleanup was the only reporter.
            // Printed on every landed import, findings or not: the pointer records what is installed. Flowline
            // never writes git itself, so the user runs the tag and push under their own rights.
            if (BuildDeployedTagHint(target.Role, usingExplicitArtifact ? null : currentCommitSha, sln.UniqueName, gateVersion) is { } tagHint)
                Console.Info(Markup.Escape(tagHint));

            var resolvedExitCode = ResolvePostImportExitCode(postImportOutcomes);
            switch (resolvedExitCode)
            {
                case ExitCode.AssemblyNotRegistered or ExitCode.PartialSuccess:
                    Console.Warning($"Deploy finished with {postImportFindings} post-import {(postImportFindings == 1 ? "finding" : "findings")} — see above.");
                    return (int)resolvedExitCode;
                case ExitCode.Inconclusive:
                    Console.Warning("Deploy landed, but verification couldn't complete — see above.");
                    return (int)ExitCode.Inconclusive;
            }

            Console.Done("Deployed! Your solution is live. (⌐■_■)");
            return 0;
        }
        finally
        {
            // Swallow cleanup failures here — a locked/in-use temp file must never mask whatever exception
            // was already propagating from the try block above.
            try
            {
                if (Directory.Exists(tmpUnpackDir))
                    Directory.Delete(tmpUnpackDir, recursive: true);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to clean up temp unpack directory {TmpUnpackDir}", tmpUnpackDir);
            }
        }
    }

    // Resolved at the top of the run, before the git-clean check and the artifact read, so an unusable
    // target is reported first. The role travels on to the environment check, which applies the
    // environment-type guard to it; a URL carries no role, so it gets none.
    internal static (string Url, EnvironmentRole? Role) ResolveTarget(string target, ProjectConfig config, bool standalone = false)
    {
        var role = EnvironmentTarget.Check(target, standalone,
            () => EnvironmentTarget.NotATarget(target, $"Run: flowline deploy {EnvironmentTarget.TargetPlaceholder}"));

        return (role is { } r ? EnvironmentTarget.RequireRoleUrl(config, r) : target, role);
    }

    // The resolved target goes into the environment check whole, so its role always reaches the
    // environment-type guard: a role must point at the right kind of environment, a URL is taken as given.
    internal static Task<(EnvironmentInfo Info, PacProfile Profile)> CheckTargetAsync(
        EnvironmentService environments, (string Url, EnvironmentRole? Role) target, CancellationToken ct) =>
        environments.CheckByUrlAsync(target.Url, target.Role, ct);

    private async Task<(EnvironmentInfo TargetEnv, bool ExistingSolution, PacProfile Profile)> ValidateTargetAsync(
        (string Url, EnvironmentRole? Role) target, ProjectSolution sln, Settings settings, CancellationToken ct)
    {
        var targetUrl = target.Url;
        var (targetEnv, profile) = await CheckTargetAsync(Environments, target, ct);

        var existingSolution = await Console.Status().FlowlineSpinner().StartAsync(
            $"Checking [bold]{sln.UniqueName}[/]...",
            _ => Validator.GetSolutionInfoAsync(targetUrl, sln.UniqueName, includeManaged: true, RuntimeOptions, settings.NoCache, ct, bypassCache: true));

        if (existingSolution != null)
        {
            if (sln.IncludeManaged && !existingSolution.IsManaged)
                throw new FlowlineException(ExitCode.ValidationFailed,
                    $"'{sln.UniqueName}' is unmanaged in {targetEnv.DisplayName} — importing managed is irreversible. Deploy solution as unmanaged.");
            if (!sln.IncludeManaged && existingSolution.IsManaged)
                throw new FlowlineException(ExitCode.ValidationFailed,
                    $"'{sln.UniqueName}' is managed in {targetEnv.DisplayName} — can't import unmanaged over managed. Deploy managed instead.");
        }

        return (targetEnv, existingSolution != null, profile);
    }

    // R5/KTD6: pure so the mode-specific wording is unit-testable without a live PAC CLI or Dataverse
    // connection, mirroring the established pattern in provision-safety-guard-unmanaged-solutions-2026-05-18.md.
    internal static string BuildFirstImportPrompt(string solutionName, string targetDisplayName, bool includeManaged) =>
        includeManaged
            ? $"First managed deploy of '{solutionName}' to {targetDisplayName} — this environment's mode can't be changed later without uninstalling the solution first. Continue?"
            : $"First deploy of '{solutionName}' to {targetDisplayName} as unmanaged — switching to managed here later needs the solution removed manually first. Continue?";

    // U2/KTD7: statement form of BuildFirstImportPrompt for --dry-run — a dry run never performs the
    // irreversible mode-lock the prompt exists to gate, so it informs instead of blocking. Pure for the
    // same reason as BuildFirstImportPrompt.
    internal static string BuildFirstImportDryRunNote(string solutionName, string targetDisplayName, bool includeManaged) =>
        includeManaged
            ? $"First managed deploy of '{solutionName}' to {targetDisplayName} — the real deploy will ask you to confirm before importing, since this mode can't change later without uninstalling first."
            : $"First deploy of '{solutionName}' to {targetDisplayName} as unmanaged — the real deploy will ask you to confirm before importing, since switching to managed later needs manual removal first.";

    // AE9/R19: a declined first-import confirmation reports ForceRequired (17), not Cancelled (130) —
    // the remedy is a flag (--force first-import), not a re-run. Pure so it's unit-testable without a
    // live PAC CLI or Dataverse connection, matching this file's established decision-method style.
    internal static ExitCode ResolveFirstImportDeclinedExitCode() => ExitCode.ForceRequired;

    // The platform's fixed name for a staged upgrade's holding solution.
    internal static string HoldingSolutionName(string solutionName) => $"{solutionName}_Upgrade";

    // Null when nothing is pending. Refuses in both cases rather than resuming a same-version stage: Flowline
    // can't prove the staged content is this package (a version match isn't proof), and applying deletes the
    // components the package dropped. Solution history holds the reason the earlier run stopped, and that
    // reason picks the remedy: a timeout leaves good content to apply, a real failure means delete and fix.
    internal static string? BuildPendingUpgradeMessage(
        string solutionName, string? holdingVersion, string incomingVersion, string targetDisplayName, string targetUrl)
    {
        if (holdingVersion == null) return null;

        var holding = HoldingSolutionName(solutionName);
        var apply = $"'pac solution upgrade --solution-name {solutionName} --environment {targetUrl}'";
        var delete = $"'pac solution delete --solution-name {holding} --environment {targetUrl}'";

        var sameVersion = Version.TryParse(holdingVersion, out var held) && Version.TryParse(incomingVersion, out var incoming)
            ? held == incoming
            : holdingVersion == incomingVersion;

        return sameVersion
            ? $"An earlier deploy staged '{solutionName}' v{holdingVersion} in {targetDisplayName} and didn't finish. " +
              $"Check Solution history in {targetDisplayName} for why. If it timed out, finish the upgrade with {apply}. " +
              $"If it failed, delete the staged upgrade with {delete}, fix the cause, and deploy again."
            : $"'{solutionName}' has a pending upgrade to v{holdingVersion} in {targetDisplayName}, and this deploy is v{incomingVersion}. " +
              $"Check Solution history in {targetDisplayName}, then apply it with {apply}, " +
              $"or delete it with {delete} (you lose data in anything it added), and deploy again.";
    }

    private async Task ValidateDtapGateAsync(
        ProjectSolution sln, string gateVersion, string targetUrl, Settings settings, CancellationToken ct)
    {
        var dtapDecision = ResolveDtapGate(Config, targetUrl);

        if (dtapDecision.Outcome == DtapGateOutcome.DevBlock)
            throw new FlowlineException(ExitCode.ValidationFailed,
                "Dev is a development environment — use 'push' to push changes there, not 'deploy'.");

        if (dtapDecision.Outcome != DtapGateOutcome.Check)
        {
            // Say so when there was no topology to check against at all. The gate resolving to Skip is
            // ordinary for a target outside the configured tiers, but a standalone run skips it for a
            // different reason — there is no config — and a promotion gate that silently does nothing is
            // exactly the shape an operator should be able to see in a CI log.
            if (!HasAnyEnvironmentUrl(Config))
                Console.Skip("Skipping DTAP gate — no environment config here to order promotions by.");
            return;
        }

        if (settings.SkipDtapCheck)
        {
            Console.Skip($"Skipping DTAP gate — '{sln.UniqueName}' not verified in {dtapDecision.PredecessorLabel}.");
            return;
        }

        // ValidateTargetAsync only resolves/guards targetUrl — the predecessor is a different
        // environment (e.g. Test when deploying to UAT) with its own pac.exe solution-list call below,
        // so it needs its own resolution to be covered by the active-profile guard (U4).
        await ProfileResolutionService.ResolveAsync(dtapDecision.PredecessorUrl!, ct);

        var predecessorInfo = await Console.Status().FlowlineSpinner().StartAsync(
            $"Checking [bold]{sln.UniqueName}[/] in {dtapDecision.PredecessorLabel}...",
            _ => Validator.GetSolutionInfoAsync(dtapDecision.PredecessorUrl!, sln.UniqueName, includeManaged: true, RuntimeOptions, settings.NoCache, ct, bypassCache: true));

        if (predecessorInfo == null)
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"'{sln.UniqueName}' hasn't been deployed to {dtapDecision.PredecessorLabel} yet — promote there first, or use --skip-dtap-check.");

        if (!DtapVersionMatches(predecessorInfo.VersionNumber, gateVersion))
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"'{sln.UniqueName}' in {dtapDecision.PredecessorLabel} environment is v{predecessorInfo.VersionNumber ?? "unknown"} — v{gateVersion} hasn't been verified there. Promote v{gateVersion} through {dtapDecision.PredecessorLabel} first, or use --skip-dtap-check.");
    }

    // Requires an exact version match, not just "predecessor is at least as new" — deliberately promoting an
    // older version than what's already verified upstream (a hotfix-style downgrade) isn't a supported flow
    // yet, so it's blocked here rather than silently allowed. --skip-dtap-check remains the manual override
    // until that's built as its own feature.
    internal static bool DtapVersionMatches(string? predecessorVersionNumber, string gateVersion) =>
        predecessorVersionNumber != null
        && Version.TryParse(predecessorVersionNumber, out var predVer)
        && Version.TryParse(gateVersion, out var localVer)
        && predVer == localVer;


    internal static void ValidateArtifactManagedFlag(bool artifactManaged, bool solutionIncludeManaged)
    {
        if (artifactManaged == solutionIncludeManaged) return;

        throw new FlowlineException(ExitCode.ValidationFailed,
            $"Artifact is {(artifactManaged ? "managed" : "unmanaged")} but the solution is configured as " +
            $"{(solutionIncludeManaged ? "managed" : "unmanaged")} — pass a matching artifact or update the solution's managed setting.");
    }

    // Catches the wrong zip before it is imported. Orphan cleanup compares the target against the
    // configured solution's source, so importing an artifact that carries a different solution deletes
    // components under a name the operator never named. Skipped in standalone, where `configured` was
    // itself derived from this same manifest and the comparison could only ever pass. A manifest with no
    // unique name at all is left to the version/identity checks that already own that case.
    internal static void ValidateArtifactSolutionName(string? artifactUniqueName, string configuredUniqueName, string artifactPath, bool standalone)
    {
        if (standalone || artifactUniqueName is null) return;
        if (string.Equals(artifactUniqueName, configuredUniqueName, StringComparison.OrdinalIgnoreCase)) return;

        throw new FlowlineException(ExitCode.ValidationFailed,
            $"'{artifactPath}' holds solution '{artifactUniqueName}', but this project is configured for '{configuredUniqueName}' — deploy the matching artifact, or run from the other project.");
    }

    // R5/R9/KTD2: standalone's only identity source — Config isn't available (it's a bare `new
    // ProjectConfig()`, see FlowlineCommand.cs), so unique name and managed both come from the artifact
    // manifest that was already read above. Pure so it's unit-testable without a live PAC CLI or
    // Dataverse connection, matching this file's established decision-method style.
    internal static ProjectSolution ResolveStandaloneSolution(string artifactPath, (string Version, bool Managed, string? UniqueName) manifest) =>
        manifest.UniqueName is { } uniqueName
            ? new ProjectSolution { UniqueName = uniqueName, IncludeManaged = manifest.Managed }
            : throw new FlowlineException(ExitCode.ValidationFailed,
                $"'{artifactPath}' has no <UniqueName> in its solution manifest — can't identify what to deploy in standalone mode.");

    // R14: standalone-only — see the call site's comment for why project mode stays silent here.
    internal static string BuildStandaloneIdentityNote(string artifactFileName) =>
        $"Standalone — no project here, identity from '{artifactFileName}'.";

    private async Task ImportSolutionAsync(string packagePath, EnvironmentInfo targetEnv, string slnName, bool stageAndUpgrade, bool publishChanges, CancellationToken ct)
    {
        var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(ct);
        var result = await Console.Status().FlowlineSpinner().StartAsync(
            $"Deploying [bold]{slnName}[/] to [bold]{targetEnv.DisplayName}[/]...",
            _ => Cli.Wrap(cmdName)
                    .WithArguments(args => args
                        .AddIfNotNull(prefixArgs)
                        .Add("solution").Add("import")
                        .Add("--path").Add(packagePath)
                        .Add("--environment").Add(targetEnv.EnvironmentUrl!)
                        .Add("--async")
                        .Add("--activate-plugins")
                        .AddIf(stageAndUpgrade, "--stage-and-upgrade")
                        .AddIf(publishChanges, "--publish-changes"))
                    .WithValidation(CommandResultValidation.None)
                    .WithCapture(_capture)
                    .ExecuteAsync(ct)
                    .Task);

        if (result.ExitCode != 0)
            throw new FlowlineException(ExitCode.BuildFailed, "Deploy failed — check the environment and your PAC login.");
    }

    // FIX A: resolves the exit code from every post-import outcome without sniffing which concrete
    // service produced which one (KTD5). Precedence, most specific first: a package assembly finding
    // (21) outranks a plain finding count (18), which outranks "couldn't verify" (19) — a deploy that
    // both failed to clean up orphans AND left an unregistered assembly reports the assembly problem,
    // since that's the one with no other remedy path.
    internal static ExitCode ResolvePostImportExitCode(IReadOnlyList<PostDeployOutcome> outcomes)
    {
        if (outcomes.Any(o => o.Findings > 0 && o.PreferredExitCode == ExitCode.AssemblyNotRegistered))
            return ExitCode.AssemblyNotRegistered;
        if (outcomes.Sum(o => o.Findings) > 0)
            return ExitCode.PartialSuccess;
        if (outcomes.Any(o => o.Inconclusive))
            return ExitCode.Inconclusive;
        return ExitCode.Success;
    }

    // U1/KTD1: dryRun takes precedence over noDelete/includeManaged — a dry run is always the most
    // restrictive mode regardless of what those two would otherwise select. Pure so it's unit-testable
    // without a live PAC CLI or Dataverse connection, matching this file's established decision-method style.
    internal static RunMode ResolveRunMode(bool dryRun, bool noDelete, bool includeManaged) =>
        dryRun ? RunMode.DryRun
        : noDelete || includeManaged ? RunMode.NoDelete
        : RunMode.Normal;

    // Null when there is no role to name the tag after (a URL target) or no commit known to point it at (the
    // --path route, whose CI step has its own commit variable). The commit is the last one touching the deploy
    // inputs, which the git-clean check has already tied to what was packed.
    internal static string? BuildDeployedTagHint(EnvironmentRole? role, string? commitSha, string solutionName, string version)
    {
        if (role is not { } r || string.IsNullOrEmpty(commitSha)) return null;

        var tag = $"deployed/{r.ToString().ToLowerInvariant()}";
        return $"Mark it in git: git tag -f -a {tag} -m \"{solutionName} {version}\" {commitSha} && git push -f origin refs/tags/{tag}";
    }

    // U3: printed once every pre-import check has passed under --dry-run — mirrors PushCommand's own
    // dry-run completion tone ("Air push complete...").
    internal static string BuildDryRunCompleteMessage(string solutionName, string targetDisplayName) =>
        $"Dry run complete — '{solutionName}' would deploy cleanly to {targetDisplayName}. Run without --dry-run to make it real.";

    internal enum DtapGateOutcome { DevBlock, Skip, Check }
    internal sealed record DtapGateDecision(DtapGateOutcome Outcome, string? PredecessorUrl = null, string? PredecessorLabel = null);

    // "No topology configured at all", which is a different thing from "this target isn't one of the
    // configured tiers". Both resolve the gate to Skip; only the first is worth announcing.
    internal static bool HasAnyEnvironmentUrl(ProjectConfig config) =>
        !string.IsNullOrEmpty(config.ProdUrl) || !string.IsNullOrEmpty(config.UatUrl) ||
        !string.IsNullOrEmpty(config.TestUrl) || !string.IsNullOrEmpty(config.DevUrl);

    internal static DtapGateDecision ResolveDtapGate(ProjectConfig config, string targetUrl)
    {
        static string Normalize(string url) => url.TrimEnd('/').ToLowerInvariant();

        var target = Normalize(targetUrl);

        bool isProd = !string.IsNullOrEmpty(config.ProdUrl) && Normalize(config.ProdUrl) == target;
        bool isUat  = !string.IsNullOrEmpty(config.UatUrl)  && Normalize(config.UatUrl)  == target;
        bool isTest = !string.IsNullOrEmpty(config.TestUrl) && Normalize(config.TestUrl) == target;
        bool isDev  = !string.IsNullOrEmpty(config.DevUrl)  && Normalize(config.DevUrl)  == target;

        if (isDev)
            return new DtapGateDecision(DtapGateOutcome.DevBlock);

        if (!isProd && !isUat && !isTest)
            return new DtapGateDecision(DtapGateOutcome.Skip);

        string? predecessorUrl = null;
        string? predecessorLabel = null;

        if (isProd)
        {
            (predecessorUrl, predecessorLabel) = FirstConfigured(EnvironmentRole.Uat, EnvironmentRole.Test, EnvironmentRole.Dev);
        }
        else if (isUat)
        {
            (predecessorUrl, predecessorLabel) = FirstConfigured(EnvironmentRole.Test, EnvironmentRole.Dev);
        }
        else if (isTest)
        {
            (predecessorUrl, predecessorLabel) = FirstConfigured(EnvironmentRole.Dev);
        }

        return string.IsNullOrEmpty(predecessorUrl)
            ? new DtapGateDecision(DtapGateOutcome.Skip)
            : new DtapGateDecision(DtapGateOutcome.Check, predecessorUrl, predecessorLabel);

        // The predecessor's URL comes from .flowline, so it gets the same check as any saved role URL.
        (string? Url, string? Label) FirstConfigured(params EnvironmentRole[] candidates) =>
            candidates.Where(r => !string.IsNullOrEmpty(config.GetUrl(r)))
                      .Select(r => ((string?)EnvironmentTarget.RequireRoleUrl(config, r), (string?)r.Label()))
                      .FirstOrDefault();
    }

    internal static string ReadLocalSolutionVersion(string dataverseSolutionFolder)
    {
        var solutionXmlPath = Path.Combine(dataverseSolutionFolder, "src", "Other", "Solution.xml");
        if (!File.Exists(solutionXmlPath))
            throw new FlowlineException(ExitCode.NotFound, $"Solution.xml not found at '{solutionXmlPath}' — run 'clone' first.");

        XDocument doc;
        try
        {
            doc = XDocument.Load(solutionXmlPath);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            throw new FlowlineException(ExitCode.ConfigInvalid,
                $"Solution.xml at '{solutionXmlPath}' is malformed or unreadable — restore " +
                $"'{ConsolePath.FormatRelativePath(dataverseSolutionFolder, markup: false)}' from git or re-run 'flowline clone'.", ex);
        }

        return ParseSolutionManifest(doc).Version;
    }

    internal static (string Version, bool Managed, string? UniqueName) ParseSolutionManifest(XDocument doc)
    {
        var manifest = doc.Root?.Element("SolutionManifest");
        var version = manifest?.Element("Version")?.Value;

        if (string.IsNullOrEmpty(version))
            throw new FlowlineException(ExitCode.ValidationFailed, "Solution version not set in Solution.xml — set a version before deploying.");

        // Managed's presence isn't confirmed against real pac output (see plan's Assumptions section) — default to
        // false rather than throw, since only Version has an established "must be present" contract today.
        var managed = manifest?.Element("Managed")?.Value == "1";

        // Unlike Version, a missing/blank UniqueName is legal here: this parser is shared with project-mode
        // callers (ReadLocalSolutionVersion, the history walk), where old revisions can predate the element.
        // Standalone mode — the only place a missing unique name is fatal — owns that check at its call site.
        var uniqueName = manifest?.Element("UniqueName")?.Value;
        uniqueName = string.IsNullOrWhiteSpace(uniqueName) ? null : uniqueName;

        return (version, managed, uniqueName);
    }

    internal static (string Version, bool Managed, string? UniqueName) ReadArtifactSolutionManifest(string zipPath)
    {
        if (!File.Exists(zipPath))
            throw new FlowlineException(ExitCode.NotFound, $"Artifact not found at '{zipPath}'.");

        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(zipPath);
        }
        catch (Exception)
        {
            // Not a zip at all (e.g. InvalidDataException for a corrupt/non-zip file) — distinct from "valid zip,
            // missing entry" below, since the former means the --path argument itself is bad input.
            throw new FlowlineException(ExitCode.ValidationFailed, $"'{zipPath}' is not a valid solution zip.");
        }

        using (archive)
        {
            var entry = FindSolutionManifestEntry(archive)
                ?? throw new FlowlineException(ExitCode.NotFound, $"No solution.xml entry found in artifact '{zipPath}' — is this a valid packed solution zip?");

            XDocument doc;
            try
            {
                using var stream = entry.Open();
                doc = XDocument.Load(stream);
            }
            catch (Exception)
            {
                // Entry exists but its content isn't well-formed XML — distinct from "entry missing" above,
                // since this means the zip is packed but corrupted rather than not a solution zip at all.
                throw new FlowlineException(ExitCode.ValidationFailed, $"'{zipPath}': {entry.FullName} is not valid XML.");
            }

            return ParseSolutionManifest(doc);
        }
    }

    // A *packed* solution zip — what `pac solution pack` produces and what --path is actually handed —
    // carries solution.xml at the root. `Other/Solution.xml` is the *unpacked source* layout, so looking
    // only there rejected every real artifact. Both are accepted: root first, since that's the packed
    // shape, then the unpacked one for a zipped-up source tree. Matching is case-insensitive because
    // ZipArchive.GetEntry is an exact string match and casing varies by producer.
    internal static ZipArchiveEntry? FindSolutionManifestEntry(ZipArchive archive) =>
        archive.Entries.FirstOrDefault(e => e.FullName.Equals("solution.xml", StringComparison.OrdinalIgnoreCase))
        ?? archive.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals("Other/Solution.xml", StringComparison.OrdinalIgnoreCase));

    // R11: how far the orphan report's verdicts can be trusted, stated from here rather than the engine
    // (KTD2 keeps OrphanCleanupService's own report commit-agnostic). This never feeds back into
    // checkoutSolutionSrcRoot or any Provenance value — it's a read-only probe run purely to decide which
    // disclaimer (if any) to print before the report.
    async Task PrintProvenanceTrustNoteAsync(bool usingExplicitArtifact, string slnFolder, string? currentCommitSha, string artifactVersion, CancellationToken ct)
    {
        if (!usingExplicitArtifact)
        {
            var note = BuildPackedRouteProvenanceNote(currentCommitSha);
            if (note != null) Console.Info(note);
            return;
        }

        // The real "inside a project" discriminator: not usingExplicitArtifact's own null `layout` (both
        // --path sub-routes leave that null, see the comment above where it's resolved), but whether a
        // solution file — and, past it, the checkout's own Solution/src — is resolvable from here at all.
        // A CI checkout carrying only the artifact (no solution file) resolves neither and falls into the
        // catch below, which is the stand-alone route.
        try
        {
            var projectLayout = await SolutionFileLayout.LoadAsync(slnFolder, ct);
            var dataverseSolutionFolder = projectLayout.DataverseSolutionFolder;
            var foundInHistory = await SolutionVersionExistsInHistoryAsync(dataverseSolutionFolder, artifactVersion, slnFolder, _capture, ct);
            Console.Warning(BuildPathInsideProjectProvenanceNote(foundInHistory, artifactVersion));
        }
        catch (FlowlineException)
        {
            Console.Skip(PathStandaloneProvenanceNote);
        }
    }

    // Packed route: dataverseSolutionFolder/src (the compare's own source) and checkoutSolutionSrcRoot
    // (what the lookup searches) are the same checkout path, and ValidateGitCleanAsync already guarantees no
    // uncommitted changes there — so the verdicts always describe exactly what's being imported. The one
    // thing that can go unconfirmed is naming which commit that is: currentCommitSha is null when
    // GetLastCommitShaForPathAsync couldn't resolve one for the deployment input paths. Null means: say so,
    // rather than implying certainty about a specific sha. Non-null means: say nothing (KTD1's silent trusted
    // case).
    internal static string? BuildPackedRouteProvenanceNote(string? currentCommitSha) =>
        currentCommitSha != null
            ? null
            : "Couldn't name the checkout's commit — orphan verdicts still describe this checkout.";

    internal static string BuildPathInsideProjectProvenanceNote(bool versionFoundInHistory, string artifactVersion) =>
        versionFoundInHistory
            ? $"Artifact v{artifactVersion} matches this checkout's history — orphan verdicts assume that build, but a version match isn't proof."
            : $"Artifact v{artifactVersion} isn't in this checkout's history — orphan verdicts describe the checkout, which may not be what this artifact holds.";

    internal const string PathStandaloneProvenanceNote = "No project source here to check against — orphan verdicts are unresolved for this deploy.";

    // R11: walks every commit that touched Other/Solution.xml in the checkout and reads each revision's
    // <Version>, stopping at the first that equals the artifact's own version. A version match is not proof
    // of provenance (two builds can share a version bump) — that caveat lives in the message above, not here.
    // A malformed historical revision is skipped rather than failing the whole probe; ParseSolutionManifest's
    // own "no Version element" throw is exactly that case.
    internal static async Task<bool> SolutionVersionExistsInHistoryAsync(
        string dataverseSolutionFolder, string version, string rootFolder, SubprocessCapture? capture, CancellationToken ct)
    {
        var gitPath = Path.GetRelativePath(rootFolder, Path.Combine(dataverseSolutionFolder, "src", "Other", "Solution.xml")).Replace('\\', '/');

        // SolutionChangeSummary.ComputeAsync's own Run helper convention: capture is optional, and every
        // invocation is suppressErrors — a probe commit or a path that never existed is an expected
        // not-found outcome here, not a tool failure worth echoing.
        Task<CliWrap.Buffered.BufferedCommandResult> Run(CliWrap.Command cmd) =>
            (capture?.Apply(cmd, suppressErrors: true) ?? cmd).ExecuteBufferedAsync(ct);

        // Bounded: Solution.xml is touched on every version bump, so a long-lived project has hundreds of
        // these commits and each one below costs its own `git show`. Searching for a version that was never
        // built here — the case this probe exists to answer "no" for — would otherwise walk the entire
        // history. The cap only ever downgrades an answer to "not found", which the caller already renders
        // as the artifact-could-not-be-placed warning, so a truncated search is reported honestly rather
        // than as a match.
        const int maxRevisionsProbed = 200;

        var logResult = await Run(
            Cli.Wrap("git")
                .WithWorkingDirectory(rootFolder)
                .WithArguments(args => args.Add("log").Add("--format=%H").Add($"--max-count={maxRevisionsProbed}").Add("--").Add(gitPath))
                .WithValidation(CommandResultValidation.None));
        if (logResult.ExitCode != 0) return false;

        var shas = logResult.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var sha in shas)
        {
            var showResult = await Run(
                Cli.Wrap("git")
                    .WithWorkingDirectory(rootFolder)
                    // `./` matters: in the <rev>:<path> form git resolves a bare path from the repository
                    // root, but a `./`-prefixed one from the working directory. Without it this probe
                    // exits 128 on every commit for a project in a repository subfolder — silently
                    // reporting "not in this checkout's history" for an artifact that is. The `git log`
                    // above needs no prefix: pathspecs are already working-directory-relative.
                    .WithArguments(args => args.Add("show").Add($"{sha}:./{gitPath}"))
                    .WithValidation(CommandResultValidation.None));
            if (showResult.ExitCode != 0) continue;

            try
            {
                var doc = XmlHelpers.Parse(showResult.StandardOutput);
                var (revisionVersion, _, _) = ParseSolutionManifest(doc);
                if (revisionVersion == version) return true;
            }
            catch (FlowlineException) { /* revision predates a Version element, or is malformed — keep looking */ }
        }

        return false;
    }
}
