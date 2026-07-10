using System.ComponentModel;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using CliWrap;
using Flowline.Config;
using Flowline.Core;
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

        [CommandOption("--solution <name>")]
        [Description("Solution to deploy (optional in project mode)")]
        public string? Solution { get; set; }

        [CommandOption("--managed")]
        [Description("Deploy the managed package (--managed false resets to default)")]
        public FlagValue<bool> Managed { get; set; } = null!;

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

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var targetUrl = ResolveTargetUrl(settings);
        var sln = Config!.GetOrUpdateSolution(settings.Solution, settings.Managed.IsSet ? settings.Managed.Value : (bool?)null, settings)
            ?? throw new FlowlineException(ExitCode.ConfigInvalid, "Solution name is required — use --solution <name>.");
        var slnFolder = Path.Combine(RootFolder, "solutions", sln.Name);
        var usingExplicitArtifact = !string.IsNullOrWhiteSpace(settings.Path);

        // --path supplies an artifact that wasn't necessarily packed from the current local tree, so neither
        // check is meaningful there: git-clean and drift both assume packagePath is derived from Package/src.
        if (!usingExplicitArtifact)
            await ValidateGitCleanAsync(sln.Name, slnFolder, cancellationToken);

        var (targetEnv, existingSolutionInTarget) = await ValidateTargetAsync(targetUrl, sln, settings, cancellationToken);

        // Resolve the DTAP gate's version cheaply (artifact manifest, cache entry, or local Solution.xml) so the
        // gate keeps failing fast before any expensive work — packing itself is deferred past the gate below.
        var candidatePackagePath = ResolveArtifactZipPath(slnFolder, sln.Name, sln.IncludeManaged);
        string gateVersion;
        ArtifactCacheEntry? reusableCacheEntry = null;
        string? currentCommitSha = null;

        if (usingExplicitArtifact)
        {
            var (artifactVersion, artifactManaged) = ReadArtifactSolutionManifest(settings.Path!);
            ValidateArtifactManagedFlag(artifactManaged, sln.IncludeManaged);
            gateVersion = artifactVersion;
        }
        else
        {
            currentCommitSha = await GitUtils.GetLastCommitShaForPathAsync(slnFolder, RootFolder, _capture, cancellationToken);
            var cacheEntry = settings.NoCache ? null : ReadCacheEntryIfExists(CacheManifestPath(candidatePackagePath));

            if (ArtifactCacheHit(cacheEntry, currentCommitSha, sln.IncludeManaged) && File.Exists(candidatePackagePath))
            {
                reusableCacheEntry = cacheEntry;
                gateVersion = cacheEntry!.Version;
            }
            else
            {
                gateVersion = ReadLocalSolutionVersion(PackageFolder(slnFolder));
            }
        }

        await ValidateDtapGateAsync(sln, gateVersion, targetUrl, settings, cancellationToken);
        ValidateLocalState(slnFolder, settings, cancellationToken, checkDrift: !usingExplicitArtifact);

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
        Logger.LogInformation("target={TargetUrl} solution={SolutionName} mode={RunMode} managed={Managed} stageAndUpgrade={StageAndUpgrade} publishChanges={PublishChanges}",
            targetUrl, sln.Name, runMode, sln.IncludeManaged, useStageAndUpgrade, publishChanges);

        var (service, _) = await ConnectToDataverseAsync(dataverseConnector, targetUrl, cancellationToken);

        string packagePath;
        if (usingExplicitArtifact)
        {
            packagePath = settings.Path!;
        }
        else if (reusableCacheEntry != null)
        {
            packagePath = candidatePackagePath;
            Console.Skip($"Reusing cached artifact for '{sln.Name}' — source unchanged since commit {reusableCacheEntry.CommitSha[..7]}.");
        }
        else
        {
            Logger.LogInformation("Packing: {SolutionName}", sln.Name);
            packagePath = await PackSolutionAsync(sln, slnFolder, candidatePackagePath, settings, cancellationToken);
            if (currentCommitSha != null)
                WriteCacheEntry(CacheManifestPath(packagePath), new ArtifactCacheEntry(gateVersion, sln.IncludeManaged, currentCommitSha));
        }

        // Always unpack the zip actually being imported — whether freshly packed, reused from cache, or
        // supplied via --path — so post-deploy services evaluate real imported content, never an assumed
        // local Package/src that may not match (e.g. a --path artifact built from a different commit).
        var tmpUnpackDir = Path.Combine(Path.GetTempPath(), "flowline-deploy-" + Guid.NewGuid());
        try
        {
            await PacUtils.UnpackSolutionAsync(packagePath, tmpUnpackDir, _capture, cancellationToken);

            var solutionInfo = new DeploySolutionInfo(sln.Name, targetEnv.EnvironmentUrl!, sln.IncludeManaged, existingSolutionInTarget);
            var postDeployContext = new PostDeployContext(service, solutionInfo, runMode, packagePath, tmpUnpackDir);

            bool IsSkipped(IPostDeployService s) =>
                settings.SkipSolutionCheck && s is SolutionCheckService ||
                settings.NoBackup && s is BackupService;

            var activeServices = postDeployServices.Where(s => !IsSkipped(s)).ToList();

            foreach (var postDeployService in activeServices)
                await postDeployService.RunPreImportAsync(postDeployContext, cancellationToken);

            Logger.LogInformation("Importing to: {TargetUrl}", targetUrl);
            await ImportSolutionAsync(packagePath, targetEnv, sln.Name, useStageAndUpgrade, publishChanges, cancellationToken);

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
            if (Directory.Exists(tmpUnpackDir))
                Directory.Delete(tmpUnpackDir, recursive: true);
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

    private async Task<(EnvironmentInfo TargetEnv, bool ExistingSolution)> ValidateTargetAsync(
        string targetUrl, ProjectSolution sln, Settings settings, CancellationToken ct)
    {
        var targetEnv = await Console.Status().FlowlineSpinner().StartAsync(
            $"Checking [bold]{targetUrl}[/]...",
            _ => FlowlineValidator.Default.GetEnvironmentInfoByUrlAsync(targetUrl, settings, ct));

        if (targetEnv == null)
            throw new FlowlineException(ExitCode.ConnectionFailed,
                "Target environment not found — check the URL or your PAC login.");

        Console.MarkupLine($"[green]Target: [bold]{targetEnv.DisplayName}[/] ({targetEnv.EnvironmentUrl})[/]");

        var existingSolution = await Console.Status().FlowlineSpinner().StartAsync(
            $"Checking [bold]{sln.Name}[/]...",
            _ => FlowlineValidator.Default.GetSolutionInfoAsync(targetUrl, sln.Name, includeManaged: true, settings, ct, bypassCache: true));

        if (existingSolution != null)
        {
            if (sln.IncludeManaged && !existingSolution.IsManaged)
                throw new FlowlineException(ExitCode.ValidationFailed,
                    $"'{sln.Name}' is unmanaged in {targetEnv.DisplayName} — importing managed is irreversible. Deploy solution as unmanaged.");
            if (!sln.IncludeManaged && existingSolution.IsManaged)
                throw new FlowlineException(ExitCode.ValidationFailed,
                    $"'{sln.Name}' is managed in {targetEnv.DisplayName} — can't import unmanaged over managed. Deploy managed instead.");
        }

        return (targetEnv, existingSolution != null);
    }

    private async Task ValidateDtapGateAsync(
        ProjectSolution sln, string localVersion, string targetUrl, Settings settings, CancellationToken ct)
    {
        var dtapDecision = ResolveDtapGate(Config!, targetUrl);

        if (dtapDecision.Outcome == DtapGateOutcome.DevBlock)
            throw new FlowlineException(ExitCode.ValidationFailed,
                "Dev is a development environment — use 'sync' to push changes there, not 'deploy'.");

        if (dtapDecision.Outcome != DtapGateOutcome.Check)
            return;

        if (settings.SkipDtapCheck)
        {
            Console.Skip($"Skipping DTAP gate — '{sln.Name}' not verified in {dtapDecision.PredecessorLabel}.");
            return;
        }

        var predecessorInfo = await Console.Status().FlowlineSpinner().StartAsync(
            $"Checking [bold]{sln.Name}[/] in {dtapDecision.PredecessorLabel}...",
            _ => FlowlineValidator.Default.GetSolutionInfoAsync(dtapDecision.PredecessorUrl!, sln.Name, includeManaged: true, settings, ct, bypassCache: true));

        if (predecessorInfo == null)
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"'{sln.Name}' hasn't been deployed to {dtapDecision.PredecessorLabel} yet — promote there first, or use --skip-dtap-check.");

        if (predecessorInfo.VersionNumber == null
            || !Version.TryParse(predecessorInfo.VersionNumber, out var predVer)
            || !Version.TryParse(localVersion, out var localVer)
            || predVer < localVer)
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"'{sln.Name}' in {dtapDecision.PredecessorLabel} environment is v{predecessorInfo.VersionNumber ?? "unknown"} — promote v{localVersion} there first, or use --skip-dtap-check.");
    }

    private async Task ValidateGitCleanAsync(string solutionName, string slnFolder, CancellationToken ct)
    {
        var changes = await GitUtils.GetUncommittedChangesInPathAsync(slnFolder, RootFolder, _capture, ct);
        if (changes.Count == 0) return;

        throw new FlowlineException(ExitCode.DirtyWorkingDirectory,
            $"Uncommitted changes in 'solutions/{solutionName}/' — commit or stash changes first before deploying.");
    }

    private void ValidateLocalState(string slnFolder, Settings settings, CancellationToken ct, bool checkDrift = true)
    {
        var cdsprojPath = Path.Combine(PackageFolder(slnFolder), $"{PackageName}.cdsproj");
        if (!File.Exists(cdsprojPath))
            throw new FlowlineException(ExitCode.NotFound,
                $"No solution found at '{cdsprojPath}' — run 'clone' first.");

        if (!checkDrift) return;

        var drift = PluginWebResourceDriftChecker.Check(slnFolder, PackageFolder(slnFolder), cancellationToken: ct)
            .Where(w => w.Category is DriftCategory.OnlyLocal or DriftCategory.PluginSizeMismatch)
            .ToList();

        if (drift.Count == 0) return;

        foreach (var w in drift)
            Console.Warning(w.Category == DriftCategory.OnlyLocal
                ? $"Only local: {w.RelativePath}"
                : $"Plugin size mismatch: {w.RelativePath}");

        if (!settings.Force)
            throw new FlowlineException(ExitCode.ValidationFailed,
                "Local changes not in Dataverse — deploy would revert them. Run 'sync' first, or use --force to skip.");
    }

    private static string ResolveArtifactZipPath(string slnFolder, string slnName, bool includeManaged)
    {
        var suffix = includeManaged ? "_managed" : "_unmanaged";
        return Path.Combine(slnFolder, "artifacts", $"{slnName}{suffix}.zip");
    }

    private static string CacheManifestPath(string packagePath) => packagePath + ".manifest.json";

    internal sealed record ArtifactCacheEntry(string Version, bool Managed, string CommitSha);

    internal static bool ArtifactCacheHit(ArtifactCacheEntry? entry, string? currentCommitSha, bool wantManaged) =>
        entry != null && currentCommitSha != null && entry.CommitSha == currentCommitSha && entry.Managed == wantManaged;

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

    private static void WriteCacheEntry(string manifestPath, ArtifactCacheEntry entry) =>
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(entry));

    internal static void ValidateArtifactManagedFlag(bool artifactManaged, bool solutionIncludeManaged)
    {
        if (artifactManaged == solutionIncludeManaged) return;

        throw new FlowlineException(ExitCode.ValidationFailed,
            $"Artifact is {(artifactManaged ? "managed" : "unmanaged")} but the solution is configured as " +
            $"{(solutionIncludeManaged ? "managed" : "unmanaged")} — pass a matching artifact or update the solution's managed setting.");
    }

    private async Task<string> PackSolutionAsync(ProjectSolution sln, string slnFolder, string packagePath, Settings settings, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);

        var packageType = sln.IncludeManaged ? "Managed" : "Unmanaged";

        var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(ct);
        var result = await Console.Status().FlowlineSpinner().StartAsync(
            $"Packing [bold]{sln.Name}[/]...",
            _ => Cli.Wrap(cmdName)
                    .WithArguments(args => args
                        .AddIfNotNull(prefixArgs)
                        .Add("solution").Add("pack")
                        .Add("--folder").Add(Path.Combine(PackageFolder(slnFolder), "src"))
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

    internal static string ReadLocalSolutionVersion(string packageFolderPath)
    {
        var solutionXmlPath = Path.Combine(packageFolderPath, "src", "Other", "Solution.xml");
        if (!File.Exists(solutionXmlPath))
            throw new FlowlineException(ExitCode.NotFound, $"Solution.xml not found at '{solutionXmlPath}' — run 'clone' first.");

        var doc = XDocument.Load(solutionXmlPath);
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
            XDocument doc;
            try
            {
                var entry = archive.GetEntry("Other/Solution.xml")
                    ?? throw new FlowlineException(ExitCode.NotFound, $"No Other/Solution.xml entry found in artifact '{zipPath}' — is this a valid packed solution zip?");

                using var stream = entry.Open();
                doc = XDocument.Load(stream);
            }
            catch (FlowlineException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new FlowlineException(ExitCode.NotFound, $"No Other/Solution.xml entry found in artifact '{zipPath}' — is this a valid packed solution zip?");
            }

            return ParseSolutionManifest(doc);
        }
    }
}
