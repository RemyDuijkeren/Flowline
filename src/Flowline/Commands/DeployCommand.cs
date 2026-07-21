using System.ComponentModel;
using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using CliWrap;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Console;
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

public class DeployCommand(IAnsiConsole console, DataverseConnector dataverseConnector, IEnumerable<IPostDeployService> postDeployServices, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService, ILoggerFactory loggerFactory, SubprocessCapture capture) : FlowlineCommand<DeployCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture)
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

        [CommandOption("--no-backup")]
        [Description("Skip the pre-deploy environment backup")]
        [DefaultValue(false)]
        public bool NoBackup { get; set; } = false;

        [CommandOption("--no-delete")]
        [Description("Report orphan components without deleting them")]
        [DefaultValue(false)]
        public bool NoDelete { get; set; } = false;
    }

    // "drift" is this command's local force hazard (skip drift validation) — distinct from the
    // unrelated `flowline drift` CLI command, which reports drift for any environment read-only.
    internal static readonly string[] ValidSpecifiers = ["drift", "first-import", "all"];
    protected override string[] ValidForceSpecifiers => ValidSpecifiers;

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var targetUrl = ResolveTargetUrl(settings);
        var sln = Config!.Solution
            ?? throw new FlowlineException(ExitCode.ConfigInvalid, "No solution configured — run 'clone' first.");
        var slnFolder = RootFolder;
        var usingExplicitArtifact = !string.IsNullOrWhiteSpace(settings.Path);

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
            var (artifactVersion, artifactManaged) = ReadArtifactSolutionManifest(settings.Path!);
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
            var prompt = BuildFirstImportPrompt(sln.UniqueName, targetEnv.DisplayName!, sln.IncludeManaged);
            if (!ConsoleHelper.Confirm(prompt, false, settings, "first-import"))
            {
                Console.Info("Deploy cancelled. Re-run with --force first-import to skip this confirmation.");
                return (int)ExitCode.Cancelled;
            }
        }

        await ValidateLocalStateAsync(slnFolder, layout, dataverseSolutionFolder, settings, cancellationToken, checkDrift: !usingExplicitArtifact);

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
        var runMode = settings.NoDelete || sln.IncludeManaged ? RunMode.NoDelete : RunMode.Normal;

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
        // flag is worth its (accepted) environment-wide cost there — Flowline's own unmanaged targets are
        // documented as single-environment/DEV-like (see wiki "Managed vs unmanaged"), so in practice there's
        // nothing else pending for that org-wide publish to sweep up anyway.
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
                cacheEntry?.Managed ?? false, sln.IncludeManaged, CiEnvironment.IsCi(), hasTestOrUat);
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
        // origin (fresh pack, cache reuse, or --path) or import outcome.
        PublishArtifactForCi(packagePath, sln.UniqueName, gateVersion);

        // Always unpack the zip actually being imported — whether freshly packed, reused from cache, or
        // supplied via --path — so post-deploy services evaluate real imported content, never an assumed
        // local package source that may not match (e.g. a --path artifact built from a different commit).
        var tmpUnpackDir = Directory.CreateTempSubdirectory("flowline-deploy-").FullName;
        try
        {
            await PacUtils.UnpackSolutionAsync(packagePath, tmpUnpackDir, _capture, cancellationToken);

            var solutionInfo = new DeploySolutionInfo(sln.UniqueName, targetEnv.EnvironmentUrl!, sln.IncludeManaged, existingSolutionInTarget);
            var postDeployContext = new PostDeployContext(service, solutionInfo, runMode, packagePath, tmpUnpackDir);

            bool IsSkipped(IPostDeployService s) =>
                settings.SkipSolutionCheck && s is SolutionCheckService ||
                settings.NoBackup && s is BackupService;

            var activeServices = postDeployServices.Where(s => !IsSkipped(s)).ToList();

            foreach (var postDeployService in activeServices)
                await postDeployService.RunPreImportAsync(postDeployContext, cancellationToken);

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

    private string ResolveTargetUrl(Settings settings)
    {
        var url = settings.Target.ToLowerInvariant() switch
        {
            "prod" => Config!.ProdUrl,
            "uat"  => Config!.UatUrl,
            "test" => Config!.TestUrl,
            "dev"  => Config!.DevUrl,
            _      => settings.Target
        };

        if (string.IsNullOrWhiteSpace(url))
            throw new FlowlineException(ExitCode.ConfigInvalid,
                $"Can't resolve '{settings.Target}' — provide an explicit URL or check your .flowline config.");

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
            ? $"[yellow]First managed deploy of '{solutionName}' to {targetDisplayName} — this environment's mode can't be changed later without uninstalling the solution first. Continue?[/]"
            : $"[yellow]First deploy of '{solutionName}' to {targetDisplayName} as unmanaged — switching to managed here later needs the solution removed manually first. Continue?[/]";

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

    private async Task ValidateLocalStateAsync(string slnFolder, SolutionFileLayout? layout, string? dataverseSolutionFolder, Settings settings, CancellationToken ct, bool checkDrift = true)
    {
        // checkDrift is false exactly on the --path route, the one route that leaves layout/dataverseSolutionFolder null.
        if (!checkDrift) return;

        // Safety-critical: a null WebResources project means the web-resource half of drift is skipped. Say
        // so loudly — a solution that does have web resources would deploy without them being validated.
        if (layout!.WebResourcesProjectPath is null)
            Console.Warning("No WebResources project resolved — skipping the web-resource drift check. If this solution has web resources, a deploy will not validate them.");

        var drift = (await PluginWebResourceDriftChecker.CheckAsync(slnFolder, layout!, dataverseSolutionFolder!, cancellationToken: ct))
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

            switch (ConsoleHelper.DetectCIPlatform())
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
                $"'{ConsolePath.FormatRelativePath(dataverseSolutionFolder)}' from git or re-run 'flowline clone'.", ex);
        }

        return ParseSolutionManifest(doc).Version;
    }

    internal static (string Version, bool Managed) ParseSolutionManifest(XDocument doc)
    {
        var manifest = doc.Root?.Element("SolutionManifest");
        var version = manifest?.Element("Version")?.Value;

        if (string.IsNullOrEmpty(version))
            throw new FlowlineException(ExitCode.ValidationFailed, "Solution version not set in Solution.xml — set a version before deploying.");

        // Managed's presence isn't confirmed against real pac output (see plan's Assumptions section) — default to
        // false rather than throw, since only Version has an established "must be present" contract today.
        var managed = manifest?.Element("Managed")?.Value == "1";

        return (version, managed);
    }

    internal static (string Version, bool Managed) ReadArtifactSolutionManifest(string zipPath)
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
            var entry = archive.GetEntry("Other/Solution.xml")
                ?? throw new FlowlineException(ExitCode.NotFound, $"No Other/Solution.xml entry found in artifact '{zipPath}' — is this a valid packed solution zip?");

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
                throw new FlowlineException(ExitCode.ValidationFailed, $"'{zipPath}': Other/Solution.xml is not valid XML.");
            }

            return ParseSolutionManifest(doc);
        }
    }
}
