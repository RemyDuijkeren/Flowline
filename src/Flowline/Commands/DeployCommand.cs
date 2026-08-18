using System.ComponentModel;
using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using CliWrap;
using CliWrap.Buffered;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Deploy;
using Flowline.Infrastructure;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using Flowline.Utils;
using Flowline.Validation;
using Spectre.Console;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class DeployCommand(IAnsiConsole console, DataverseConnector dataverseConnector, IEnumerable<IPostDeployService> postDeployServices, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService, ILoggerFactory loggerFactory, SubprocessCapture capture, NuGetVersionClient nuGetVersionClient) : FlowlineCommand<DeployCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture, nuGetVersionClient)
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "<target>")]
        [Description("Target environment: prod, uat, test, dev, or a URL")]
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
        var standalone = IsStandalone(settings);
        var targetUrl = ResolveTargetUrl(settings, standalone);

        // U3/KTD2: standalone only, and hoisted this high only because standalone builds its
        // ProjectSolution from this manifest instead of Config, and `sln` is consumed below (target
        // validation, artifact path) before the `usingExplicitArtifact` branch that used to own this read.
        // Deliberately NOT hoisted for --path inside a project (R2: that route must behave exactly as it
        // does today). Hoisting it there too would move a corrupt-zip failure ahead of ValidateTargetAsync,
        // so the `Target: <env>` line and its round-trip would stop happening first — a visible reordering
        // of project-mode output, which the Goal Capsule puts out of scope. That route keeps reading the
        // manifest at its original point below; each route still parses the zip exactly once.
        var artifactManifest = standalone
            ? ReadArtifactSolutionManifest(settings.Path!)
            : ((string Version, bool Managed, string? UniqueName)?)null;

        var sln = standalone
            ? ResolveStandaloneSolution(settings.Path!, artifactManifest!.Value)
            : Config!.Solution ?? throw new FlowlineException(ExitCode.ConfigInvalid, "No solution configured — run 'clone' first.");

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
        if (!usingExplicitArtifact)
        {
            deploymentInputPaths = GetDeploymentInputPaths(layout!, dataverseSolutionFolder!); // non-null on the packed route
            await ValidateGitCleanAsync(deploymentInputPaths, cancellationToken);
        }

        var (targetEnv, existingSolutionInTarget, resolvedProfile) = await ValidateTargetAsync(targetUrl, sln, settings, cancellationToken);

        // Resolve the DTAP gate's version cheaply (artifact manifest, cache entry, or local Solution.xml) so the
        // gate keeps failing fast before any expensive work — packing itself is deferred past the gate below.
        var candidatePackagePath = ResolveArtifactZipPath(slnFolder, sln.UniqueName, sln.IncludeManaged);
        string gateVersion;
        ArtifactCacheEntry? cacheEntry = null;
        string? currentCommitSha = null;
        var cacheOutcome = CacheOutcome.NoEntry;

        if (usingExplicitArtifact)
        {
            // Standalone already read this above (it needed the unique name to build `sln` at all);
            // --path inside a project reads it here, exactly where it always did. Either way the zip is
            // parsed once per run, so one deploy can never act on two answers.
            var (artifactVersion, artifactManaged, _) = artifactManifest ?? ReadArtifactSolutionManifest(settings.Path!);
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
            cacheEntry = ReadCacheEntryIfExists(CacheManifestPath(candidatePackagePath));
            cacheOutcome = ResolveCacheOutcome(cacheEntry, currentCommitSha, sln.IncludeManaged, settings.NoCache, File.Exists(candidatePackagePath));

            gateVersion = cacheOutcome == CacheOutcome.Hit
                ? cacheEntry!.Version
                : ReadLocalSolutionVersion(dataverseSolutionFolder!); // non-null on the packed route
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
            else if (!await AnsiConsole.Console.ConfirmAsync(BuildFirstImportPrompt(sln.UniqueName, targetEnv.DisplayName!, sln.IncludeManaged), false, settings, "first-import", cancellationToken))
            {
                Console.Info("Deploy cancelled. Re-run with --force first-import to skip this confirmation.");
                return (int)ExitCode.Cancelled;
            }
        }

        await ValidateLocalStateAsync(sln.UniqueName, layout, dataverseSolutionFolder, settings, cancellationToken, checkDrift: !usingExplicitArtifact);

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
        Logger.LogInformation("target={TargetUrl} solution={SolutionName} mode={RunMode} managed={Managed} stageAndUpgrade={StageAndUpgrade} publishChanges={PublishChanges} cacheOutcome={CacheOutcome}",
            targetUrl, sln.UniqueName, runMode, sln.IncludeManaged, useStageAndUpgrade, publishChanges, usingExplicitArtifact ? (CacheOutcome?)null : cacheOutcome);

        var (service, _) = await ConnectToDataverseAsync(dataverseConnector, targetUrl, cancellationToken, resolvedProfile);

        string packagePath;
        if (usingExplicitArtifact)
        {
            packagePath = settings.Path!;
        }
        else
        {
            var hasTestOrUat = !string.IsNullOrEmpty(Config!.TestUrl) || !string.IsNullOrEmpty(Config.UatUrl);
            var cacheMessage = BuildCacheStatusMessage(cacheOutcome, sln.UniqueName, cacheEntry?.CommitSha, currentCommitSha,
                // Only shapes the message's wording (pipeline framing is noise in CI) — never gates a
                // prompt, so an env-var probe is right here rather than a console capability check.
                cacheEntry?.Managed ?? false, sln.IncludeManaged, CiPlatform.Detect() is not null, hasTestOrUat);
            if (cacheOutcome == CacheOutcome.Hit)
                Console.Skip(cacheMessage);
            else
                Console.Info(cacheMessage);

            if (cacheOutcome == CacheOutcome.Hit)
            {
                packagePath = candidatePackagePath;
            }
            else
            {
                Logger.LogInformation("Packing: {SolutionName}", sln.UniqueName);
                packagePath = await PackSolutionAsync(sln, dataverseSolutionFolder!, candidatePackagePath, settings, cancellationToken); // non-null on the packed route
                if (currentCommitSha != null)
                    WriteCacheEntry(CacheManifestPath(packagePath), new ArtifactCacheEntry(gateVersion, sln.IncludeManaged, currentCommitSha));
            }
        }

        // R5: fires regardless of whether the subsequent import succeeds — the packed zip is already
        // valid and potentially useful (manual retry, inspection) once it's resolved, independent of
        // origin (fresh pack, cache reuse, or --path) or import outcome. U3: never fires under --dry-run —
        // this signal hands a packed artifact off to a later pipeline stage for real promotion, and
        // --dry-run never produces one.
        if (!settings.DryRun)
            PublishArtifactForCi(packagePath, sln.UniqueName, gateVersion);

        // Always unpack the zip actually being imported — whether freshly packed, reused from cache, or
        // supplied via --path — so post-deploy services evaluate real imported content, never an assumed
        // local package source that may not match (e.g. a --path artifact built from a different commit).
        var tmpUnpackDir = Directory.CreateTempSubdirectory("flowline-deploy-").FullName;
        try
        {
            await PacUtils.UnpackSolutionAsync(packagePath, tmpUnpackDir, sln.IncludeManaged, _capture, cancellationToken);

            var solutionInfo = new DeploySolutionInfo(sln.UniqueName, targetEnv.EnvironmentUrl!, sln.IncludeManaged, existingSolutionInTarget);
            // KTD2: the checkout's own src/ — distinct from tmpUnpackDir above (the actually-imported
            // content, whether freshly packed, cache-reused, or --path). Null on the --path route, which
            // leaves dataverseSolutionFolder unresolved (no solution file needed there) — the provenance
            // lookup then reads every entry as Undetermined rather than guessing a path.
            var checkoutSolutionSrcRoot = dataverseSolutionFolder != null ? Path.Combine(dataverseSolutionFolder, "src") : null;
            var postDeployContext = new PostDeployContext(service, solutionInfo, runMode, packagePath, tmpUnpackDir, settings.HasForce("delete-orphans"), checkoutSolutionSrcRoot);

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

            var cleanupFailures = 0;
            foreach (var postDeployService in activeServices)
                cleanupFailures += await postDeployService.RunPostImportAsync(postDeployContext, cancellationToken);
            Logger.LogInformation("Post-deploy cleanup: {Failures} failures", cleanupFailures);

            if (ShouldReportPartialSuccess(cleanupFailures))
            {
                Console.Warning($"{cleanupFailures} orphan {(cleanupFailures == 1 ? "component" : "components")} couldn't be cleaned up — see above, remove manually via maker portal.");
                return (int)ExitCode.PartialSuccess;
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

    private string ResolveTargetUrl(Settings settings, bool standalone) => ResolveTargetUrl(settings.Target, Config!, standalone);

    // R15: `standalone` only reshapes the "can't resolve" message below — a role keyword resolves the
    // same way either way, since Config is `new ProjectConfig()` (all URLs empty) in standalone, so
    // every role keyword falls through to that throw there. Default false keeps every existing
    // project-mode call site (and test) unchanged.
    internal static string ResolveTargetUrl(string target, ProjectConfig config, bool standalone = false)
    {
        var url = target.ToLowerInvariant() switch
        {
            "prod" => config.ProdUrl,
            "uat"  => config.UatUrl,
            "test" => config.TestUrl,
            "dev"  => config.DevUrl,
            _      => target
        };

        if (string.IsNullOrWhiteSpace(url))
            throw new FlowlineException(ExitCode.ConfigInvalid,
                standalone
                    ? $"Can't resolve '{target}' — no config here to check (standalone mode). Pass an explicit URL instead."
                    : $"Can't resolve '{target}' — provide an explicit URL or check your .flowline config.");

        // Anything that isn't prod/uat/test/dev falls through as a literal URL above — reject garbage here
        // rather than letting it reach MSAL as a token scope, which fails with an opaque AADSTS error.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"'{target}' isn't a known target (prod, uat, test, dev) or a valid URL.");

        return url;
    }

    private async Task<(EnvironmentInfo TargetEnv, bool ExistingSolution, PacProfile Profile)> ValidateTargetAsync(
        string targetUrl, ProjectSolution sln, Settings settings, CancellationToken ct)
    {
        var profile = await ProfileResolutionService.ResolveAsync(targetUrl, ct);
        var targetEnv = await Console.Status().FlowlineSpinner().StartAsync(
            $"Checking [bold]{targetUrl}[/]...",
            _ => FlowlineValidator.Default.GetEnvironmentInfoByUrlAsync(targetUrl, profile, settings, ct));

        if (targetEnv == null)
            throw new FlowlineException(ExitCode.ConnectionFailed,
                "Target environment not found — check the URL or your PAC login.");

        Console.MarkupLine($"[green]Target: [bold]{targetEnv.DisplayName}[/] ({targetEnv.EnvironmentUrl})[/]");

        var existingSolution = await Console.Status().FlowlineSpinner().StartAsync(
            $"Checking [bold]{sln.UniqueName}[/]...",
            _ => FlowlineValidator.Default.GetSolutionInfoAsync(targetUrl, sln.UniqueName, includeManaged: true, settings, ct, bypassCache: true));

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

    private async Task ValidateDtapGateAsync(
        ProjectSolution sln, string gateVersion, string targetUrl, Settings settings, CancellationToken ct)
    {
        var dtapDecision = ResolveDtapGate(Config!, targetUrl);

        if (dtapDecision.Outcome == DtapGateOutcome.DevBlock)
            throw new FlowlineException(ExitCode.ValidationFailed,
                "Dev is a development environment — use 'sync' to push changes there, not 'deploy'.");

        if (dtapDecision.Outcome != DtapGateOutcome.Check)
            return;

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
            _ => FlowlineValidator.Default.GetSolutionInfoAsync(dtapDecision.PredecessorUrl!, sln.UniqueName, includeManaged: true, settings, ct, bypassCache: true));

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

    private async Task ValidateGitCleanAsync(IReadOnlyList<string> deploymentInputPaths, CancellationToken ct)
    {
        var changes = await GitUtils.GetUncommittedChangesInPathAsync(deploymentInputPaths, RootFolder, _capture, ct);
        if (changes.Count == 0) return;

        // Names the files rather than the folders it looked in — the scope is discovered now, so restating
        // it would mean restating a list the user can't predict from the message.
        var shown = string.Join(", ", changes.Take(3));
        var more = changes.Count > 3 ? $" (+{changes.Count - 3} more)" : "";

        throw new FlowlineException(ExitCode.DirtyWorkingDirectory,
            $"Uncommitted changes in {shown}{more} — commit or stash first, then deploy.");
    }

    // R15: the SAME path list scopes both the clean-check and the artifact-cache commit-sha lookup, so the
    // two can never diverge — resolved once per run in ExecuteFlowlineAsync and handed to both, which also
    // keeps the solution file read to one per deploy (R4).
    //
    // Deliberately narrow to what actually affects the packed artifact: Solution/, every plugin project the
    // solution file references, and the WebResources project file. The plugin pre-filter is what keeps this
    // narrow — an unrelated csproj in the solution stays out of the cache key, so it can't invalidate a deploy.
    //
    // Takes no solution name: all three project paths come out of the already-loaded layout, so a relocated
    // or renamed project stays in scope without deploy knowing what it is called. Synchronous — the layout
    // is already resolved by the time this runs, so there is no I/O left to await.
    internal static IReadOnlyList<string> GetDeploymentInputPaths(SolutionFileLayout layout, string dataverseSolutionFolder) =>
    [
        dataverseSolutionFolder,
        ..layout.PluginProjects.Select(c => c.ProjectPath),
        // Omitted when null: no WebResources project is a real absence, not a scope gap.
        ..layout.WebResourcesProjectPath is { } wr ? new[] { wr } : Array.Empty<string>()
    ];

    private async Task ValidateLocalStateAsync(string solutionUniqueName, SolutionFileLayout? layout, string? dataverseSolutionFolder, Settings settings, CancellationToken ct, bool checkDrift = true)
    {
        // checkDrift is false exactly on the --path route, the one route that leaves layout/dataverseSolutionFolder null.
        if (!checkDrift) return;

        // Safety-critical: a null WebResources project means the web-resource half of drift is skipped. Say
        // so loudly — a solution that does have web resources would deploy without them being validated.
        if (layout!.WebResourcesProjectPath is null)
            Console.Warning("No WebResources project resolved — skipping the web-resource drift check. If this solution has web resources, a deploy will not validate them.");

        var drift = (await PluginWebResourceDriftChecker.CheckAsync(solutionUniqueName, layout!, dataverseSolutionFolder!, cancellationToken: ct))
            .Where(w => w.Category is DriftCategory.OnlyLocal or DriftCategory.PluginSizeMismatch)
            .ToList();

        if (drift.Count == 0) return;

        foreach (var w in drift)
            Console.Warning(w.Category == DriftCategory.OnlyLocal
                ? $"Only local: {w.RelativePath}"
                : $"Plugin size mismatch: {w.RelativePath}");

        if (!settings.HasForce("drift"))
            throw new FlowlineException(ExitCode.ValidationFailed,
                "Local changes not in Dataverse — deploy would revert them. Run 'push' then 'sync' to capture them, or use --force drift to skip.");
    }

    private static string ResolveArtifactZipPath(string slnFolder, string slnName, bool includeManaged)
    {
        var suffix = includeManaged ? "_managed" : "_unmanaged";
        return Path.Combine(slnFolder, "artifacts", $"{slnName}{suffix}.zip");
    }

    private static string CacheManifestPath(string packagePath) => packagePath + ".manifest.json";

    internal sealed record ArtifactCacheEntry(string Version, bool Managed, string CommitSha);

    internal enum CacheOutcome
    {
        Hit,
        NoEntry,
        CommitChanged,
        NoCurrentCommit,
        ManagedMismatch,
        NoCacheFlag,
        ArtifactFileMissing
    }

    // KTD6: precedence mirrors the old ArtifactCacheHit short-circuit order — the first condition that
    // applies names the reason; this never reports more than one.
    internal static CacheOutcome ResolveCacheOutcome(ArtifactCacheEntry? entry, string? currentCommitSha, bool wantManaged, bool noCache, bool artifactFileExists)
    {
        if (noCache) return CacheOutcome.NoCacheFlag;
        if (entry == null) return CacheOutcome.NoEntry;
        if (currentCommitSha == null) return CacheOutcome.NoCurrentCommit;
        if (entry.CommitSha != currentCommitSha) return CacheOutcome.CommitChanged;
        if (entry.Managed != wantManaged) return CacheOutcome.ManagedMismatch;
        if (!artifactFileExists) return CacheOutcome.ArtifactFileMissing;
        return CacheOutcome.Hit;
    }

    // KTD4/KTD5: pure so the outcome/CI/Test-UAT branching is unit-testable without a live PAC CLI or
    // Dataverse connection. The pipeline-style framing only appears when hasTestOrUat and never on CI —
    // most CI runners are ephemeral per stage, so the "reused across every promotion stage" framing
    // wouldn't hold even when this particular run genuinely hit the cache (a self-hosted or
    // persisted-workspace runner can); the CI note is appended to whatever outcome actually resolved
    // to, never a replacement for it.
    internal static string BuildCacheStatusMessage(CacheOutcome outcome, string solutionName, string? cachedCommitSha,
        string? currentCommitSha, bool cachedManaged, bool wantManaged, bool isCi, bool hasTestOrUat)
    {
        var showPipelineFraming = hasTestOrUat && !isCi;
        const string reusedAcrossStages = " Built once, reused across every promotion stage until source changes.";
        const string willBeReused = " This build will be reused across later promotion stages unless source changes.";

        var message = outcome switch
        {
            CacheOutcome.Hit =>
                $"Reusing cached artifact for '{solutionName}' — source unchanged since commit {cachedCommitSha![..7]}."
                + (showPipelineFraming ? reusedAcrossStages : ""),
            CacheOutcome.NoEntry =>
                $"No cached build yet for '{solutionName}' — packing now."
                + (showPipelineFraming ? willBeReused : ""),
            CacheOutcome.CommitChanged =>
                $"Packing '{solutionName}' — source changed since the cached build (commit {cachedCommitSha![..7]} -> {currentCommitSha![..7]})."
                + (showPipelineFraming ? willBeReused : ""),
            CacheOutcome.ManagedMismatch =>
                $"Packing '{solutionName}' — cached build was {(cachedManaged ? "managed" : "unmanaged")}, this deploy wants {(wantManaged ? "managed" : "unmanaged")}."
                + (showPipelineFraming ? willBeReused : ""),
            CacheOutcome.NoCacheFlag =>
                $"Packing '{solutionName}' — --no-cache forced a fresh pack."
                + (showPipelineFraming ? willBeReused : ""),
            CacheOutcome.ArtifactFileMissing =>
                $"Packing '{solutionName}' — the cached manifest exists but the artifact file is missing."
                + (showPipelineFraming ? willBeReused : ""),
            CacheOutcome.NoCurrentCommit =>
                $"Packing '{solutionName}' — couldn't resolve the current commit."
                + (showPipelineFraming ? willBeReused : ""),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };

        if (isCi)
            message += " On CI, when each DTAP stage runs on its own ephemeral runner, this cache can't carry a build between stages — use --path to reuse one build across them instead.";

        return message;
    }

    // KTD3: Azure Pipelines' documented stdout logging-command protocol for any process — no SDK, no opt-in flag.
    // The artifact name (not the underlying file) carries the version, so it's visible at a glance in the
    // pipeline's Artifacts tab without making the on-disk zip's filename — load-bearing for the artifact-reuse
    // cache and --path — version-dependent.
    internal static string BuildAzureDevOpsArtifactUploadLine(string packagePath, string solutionName, string version) =>
        $"##vso[artifact.upload artifactname={solutionName}-{version}]{packagePath}";

    // KTD4/KTD6: qualified by solution name so looping deploy over sibling solutions in one workflow step
    // doesn't have each write silently clobber the previous solution's $GITHUB_OUTPUT key.
    internal static string BuildGitHubActionsOutputLine(string packagePath, string solutionName) =>
        $"artifact-path-{solutionName}={packagePath}";

    // KTD2: called once packagePath is finalized, regardless of origin (fresh pack, cache reuse, --path)
    // or subsequent import outcome (R5). KTD5/R4: never lets a CI-integration side effect fail the deploy.
    private void PublishArtifactForCi(string packagePath, string solutionName, string version)
    {
        try
        {
            // Resolved once, absolute for both platforms — packagePath can be relative (a --path deploy
            // takes settings.Path verbatim), and neither CI consumer should have to guess it's relative
            // to whatever directory the agent happened to run the command from.
            var fullPackagePath = Path.GetFullPath(packagePath);

            switch (CiPlatform.Detect())
            {
                case "azuredevops":
                    // KTD3: raw System.Console, never the injected IAnsiConsole — Console.MarkupLine would
                    // parse the vso line's literal "[artifact.upload ...]" as a Spectre style tag and throw,
                    // and even a plain IAnsiConsole write can word-wrap this line (it contains a space)
                    // across physical lines once redirected stdout falls back to an 80-column profile width
                    // on a real agent, which silently breaks the agent's single-line ##vso parse.
                    System.Console.WriteLine(BuildAzureDevOpsArtifactUploadLine(fullPackagePath, solutionName, version));
                    break;
                case "github":
                    var githubOutputPath = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
                    if (!string.IsNullOrEmpty(githubOutputPath))
                        File.AppendAllText(githubOutputPath, BuildGitHubActionsOutputLine(fullPackagePath, solutionName) + Environment.NewLine);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to publish CI artifact signal for {SolutionName}", solutionName);
        }
    }

    internal static ArtifactCacheEntry? ReadCacheEntryIfExists(string manifestPath)
    {
        if (!File.Exists(manifestPath)) return null;

        try
        {
            return JsonSerializer.Deserialize<ArtifactCacheEntry>(File.ReadAllText(manifestPath));
        }
        catch (Exception)
        {
            // Corrupt or partially-written sidecar (e.g. a prior process was killed mid-write) — the tool owns
            // this file, not the user, so treat it the same as absent rather than crashing an ordinary deploy.
            return null;
        }
    }

    private static void WriteCacheEntry(string manifestPath, ArtifactCacheEntry entry)
    {
        try
        {
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(entry));
        }
        catch (Exception)
        {
            // A failed cache write shouldn't fail a deploy that already packed successfully — worst case,
            // the next deploy just doesn't find this entry and repacks, matching ReadCacheEntryIfExists's
            // own tolerance for a missing/corrupt sidecar.
        }
    }

    internal static void ValidateArtifactManagedFlag(bool artifactManaged, bool solutionIncludeManaged)
    {
        if (artifactManaged == solutionIncludeManaged) return;

        throw new FlowlineException(ExitCode.ValidationFailed,
            $"Artifact is {(artifactManaged ? "managed" : "unmanaged")} but the solution is configured as " +
            $"{(solutionIncludeManaged ? "managed" : "unmanaged")} — pass a matching artifact or update the solution's managed setting.");
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

    private async Task<string> PackSolutionAsync(ProjectSolution sln, string dataverseSolutionFolder, string packagePath, Settings settings, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);

        var packageType = sln.IncludeManaged ? "Managed" : "Unmanaged";

        var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(ct);
        var result = await Console.Status().FlowlineSpinner().StartAsync(
            $"Packing [bold]{sln.UniqueName}[/]...",
            _ => Cli.Wrap(cmdName)
                    .WithArguments(args => args
                        .AddIfNotNull(prefixArgs)
                        .Add("solution").Add("pack")
                        .Add("--folder").Add(Path.Combine(dataverseSolutionFolder, "src"))
                        .Add("--zipFile").Add(packagePath)
                        .Add("--packageType").Add(packageType))
                    .WithValidation(CommandResultValidation.None)
                    .WithCapture(_capture)
                    .ExecuteAsync(ct)
                    .Task);

        if (result.ExitCode != 0)
            throw new FlowlineException(ExitCode.BuildFailed, "Pack failed — check your solution source.");

        return packagePath;
    }

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

    internal static bool ShouldReportPartialSuccess(int cleanupFailures) => cleanupFailures > 0;

    // U1/KTD1: dryRun takes precedence over noDelete/includeManaged — a dry run is always the most
    // restrictive mode regardless of what those two would otherwise select. Pure so it's unit-testable
    // without a live PAC CLI or Dataverse connection, matching this file's established decision-method style.
    internal static RunMode ResolveRunMode(bool dryRun, bool noDelete, bool includeManaged) =>
        dryRun ? RunMode.DryRun
        : noDelete || includeManaged ? RunMode.NoDelete
        : RunMode.Normal;

    // U3: printed once every pre-import check has passed under --dry-run — mirrors PushCommand's own
    // dry-run completion tone ("Air push complete...").
    internal static string BuildDryRunCompleteMessage(string solutionName, string targetDisplayName) =>
        $"Dry run complete — '{solutionName}' would deploy cleanly to {targetDisplayName}. Run without --dry-run to make it real.";

    internal enum DtapGateOutcome { DevBlock, Skip, Check }
    internal sealed record DtapGateDecision(DtapGateOutcome Outcome, string? PredecessorUrl = null, string? PredecessorLabel = null);

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
            (predecessorUrl, predecessorLabel) = FirstConfigured(
                (config.UatUrl, "UAT"),
                (config.TestUrl, "Test"),
                (config.DevUrl, "Dev"));
        }
        else if (isUat)
        {
            (predecessorUrl, predecessorLabel) = FirstConfigured(
                (config.TestUrl, "Test"),
                (config.DevUrl, "Dev"));
        }
        else if (isTest)
        {
            (predecessorUrl, predecessorLabel) = FirstConfigured(
                (config.DevUrl, "Dev"));
        }

        return string.IsNullOrEmpty(predecessorUrl)
            ? new DtapGateDecision(DtapGateOutcome.Skip)
            : new DtapGateDecision(DtapGateOutcome.Check, predecessorUrl, predecessorLabel);

        static (string? Url, string? Label) FirstConfigured(params (string? Url, string Label)[] candidates) =>
            candidates.Select(c => (c.Url, c.Label))
                      .FirstOrDefault(c => !string.IsNullOrEmpty(c.Url));
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

    // Packed/cached route: dataverseSolutionFolder/src (the compare's own source) and checkoutSolutionSrcRoot
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
                    .WithArguments(args => args.Add("show").Add($"{sha}:{gitPath}"))
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
