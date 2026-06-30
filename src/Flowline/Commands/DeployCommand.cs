using System.ComponentModel;
using System.Xml.Linq;
using CliWrap;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Services;
using Flowline.Utils;
using Flowline.Validation;
using Spectre.Console;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class DeployCommand(IAnsiConsole console, DataverseConnector dataverseConnector, OrphanCleanupService orphanCleanupService, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService, ILoggerFactory loggerFactory, SubprocessCapture capture) : FlowlineCommand<DeployCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture)
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "<target>")]
        [Description("Target environment: prod, uat, test, dev, or a URL")]
        public string Target { get; set; } = null!;

        [CommandOption("--solution <name>")]
        [Description("Solution to deploy")]
        public string? Solution { get; set; }

        [CommandOption("--managed")]
        [Description("Deploy the managed package (--managed false resets to default)")]
        public FlagValue<bool> Managed { get; set; } = null!;

        [CommandOption("--skip-dtap-check")]
        [Description("Skip DTAP promotion checks")]
        [DefaultValue(false)]
        public bool SkipDtapCheck { get; set; } = false;

        [CommandOption("--no-delete")]
        [Description("Report orphan components without deleting them")]
        [DefaultValue(false)]
        public bool NoDelete { get; set; } = false;
    }

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        await GitUtils.AssertRepoCleanAsync(_capture, cancellationToken);

        var runMode = settings.NoDelete ? RunMode.NoDelete : RunMode.Normal;
        var targetUrl = ResolveTargetUrl(settings);
        var sln = Config!.GetOrUpdateSolution(settings.Solution, settings.Managed.IsSet ? settings.Managed.Value : (bool?)null, settings)
            ?? throw new FlowlineException(ExitCode.ConfigInvalid, "Solution name is required — use --solution <name>.");
        var slnFolder = Path.Combine(RootFolder, "solutions", sln.Name);
        Logger.LogInformation("target={TargetUrl} solution={SolutionName} mode={RunMode} managed={Managed}", targetUrl, sln.Name, runMode, sln.IncludeManaged);

        var targetEnv = await ValidateTargetAsync(targetUrl, sln, settings, cancellationToken);
        await ValidateDtapGateAsync(sln, slnFolder, targetUrl, settings, cancellationToken);
        ValidateLocalState(slnFolder, settings, cancellationToken);

        var sNew = ParseSolutionXml(slnFolder);
        var (service, _) = await ConnectToDataverseAsync(dataverseConnector, targetUrl, cancellationToken);
        var webresourceRoot = Path.Combine(slnFolder, "WebResources");
        var deferred = await orphanCleanupService.RunPreImportAsync(service, sln.Name, sNew, runMode, webresourceRoot, cancellationToken);

        Logger.LogInformation("Packing: {SolutionName}", sln.Name);
        var packagePath = await PackSolutionAsync(sln, slnFolder, settings, cancellationToken);
        Logger.LogInformation("Importing to: {TargetUrl}", targetUrl);
        await ImportSolutionAsync(packagePath, targetEnv, sln.Name, cancellationToken);
        var cleanupFailures = await orphanCleanupService.RunPostImportAsync(service, sln.Name, sNew, deferred, runMode, cancellationToken);
        Logger.LogInformation("Orphan cleanup: {Failures} failures", cleanupFailures);

        if (cleanupFailures > 0)
        {
            Console.Warning($"{cleanupFailures} orphan {(cleanupFailures == 1 ? "component" : "components")} couldn't be cleaned up — see above, remove manually via maker portal.");
            return (int)ExitCode.PartialSuccess;
        }

        Console.Done("Deployed! Your solution is live. (⌐■_■)");
        return 0;
    }

    // ── Target resolution ────────────────────────────────────────────────────

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

    // ── Validation ───────────────────────────────────────────────────────────

    private async Task<EnvironmentInfo> ValidateTargetAsync(
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
            _ => FlowlineValidator.Default.GetSolutionInfoAsync(targetUrl, sln.Name, includeManaged: true, settings, ct));

        if (existingSolution != null)
        {
            if (sln.IncludeManaged && !existingSolution.IsManaged)
                throw new FlowlineException(ExitCode.ValidationFailed,
                    $"'{sln.Name}' is unmanaged in {targetEnv.DisplayName} — importing managed is irreversible. Deploy solution as unmanaged.");
            if (!sln.IncludeManaged && existingSolution.IsManaged)
                throw new FlowlineException(ExitCode.ValidationFailed,
                    $"'{sln.Name}' is managed in {targetEnv.DisplayName} — can't import unmanaged over managed. Deploy managed instead.");
        }

        return targetEnv;
    }

    private async Task ValidateDtapGateAsync(
        ProjectSolution sln, string slnFolder, string targetUrl, Settings settings, CancellationToken ct)
    {
        var dtapDecision = ResolveDtapGate(Config!, targetUrl);

        if (dtapDecision.Outcome == DtapGateOutcome.DevBlock)
            throw new FlowlineException(ExitCode.ValidationFailed,
                "Dev is a development environment — use 'sync' to push changes there, not 'deploy'.");

        if (dtapDecision.Outcome != DtapGateOutcome.Check)
            return;

        var localVersion = ReadLocalSolutionVersion(PackageFolder(slnFolder));

        if (settings.SkipDtapCheck)
        {
            Console.Skip($"Skipping DTAP gate — '{sln.Name}' not verified in {dtapDecision.PredecessorLabel}.");
            return;
        }

        var predecessorInfo = await Console.Status().FlowlineSpinner().StartAsync(
            $"Checking [bold]{sln.Name}[/] in {dtapDecision.PredecessorLabel}...",
            _ => FlowlineValidator.Default.GetSolutionInfoAsync(dtapDecision.PredecessorUrl!, sln.Name, includeManaged: true, settings, ct));

        if (predecessorInfo == null)
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"'{sln.Name}' hasn't been deployed to {dtapDecision.PredecessorLabel} yet — promote there first, or use --skip-dtap-check.");

        if (predecessorInfo.VersionNumber == null
            || !Version.TryParse(predecessorInfo.VersionNumber, out var predVer)
            || !Version.TryParse(localVersion, out var localVer)
            || predVer < localVer)
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"'{sln.Name}' in {dtapDecision.PredecessorLabel} is v{predecessorInfo.VersionNumber ?? "unknown"} — promote v{localVersion} there first, or use --skip-dtap-check.");
    }

    private void ValidateLocalState(string slnFolder, Settings settings, CancellationToken ct)
    {
        var cdsprojPath = Path.Combine(PackageFolder(slnFolder), $"{PackageName}.cdsproj");
        if (!File.Exists(cdsprojPath))
            throw new FlowlineException(ExitCode.NotFound,
                $"No solution found at '{cdsprojPath}' — run 'clone' first.");

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

    private IReadOnlyList<(Guid ObjectId, int ComponentType)> ParseSolutionXml(string slnFolder)
    {
        try
        {
            return ComponentClassifier.ParseSolutionXmlComponents(
                Path.Combine(PackageFolder(slnFolder), "src", "Other", "Solution.xml"));
        }
        catch (FileNotFoundException ex)
        {
            throw new FlowlineException(ExitCode.NotFound, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw new FlowlineException(ExitCode.ValidationFailed, ex.Message);
        }
    }

    // ── Pack and import ──────────────────────────────────────────────────────

    private async Task<string> PackSolutionAsync(ProjectSolution sln, string slnFolder, Settings settings, CancellationToken ct)
    {
        var artifactsFolder = Path.Combine(slnFolder, "artifacts");
        Directory.CreateDirectory(artifactsFolder);

        var suffix      = sln.IncludeManaged ? "_managed" : "_unmanaged";
        var packageType = sln.IncludeManaged ? "Managed" : "Unmanaged";
        var packagePath = Path.Combine(artifactsFolder, $"{sln.Name}{suffix}.zip");

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
                    .WithToolExecutionLog(RuntimeOptions)
                    .ExecuteAsync(ct)
                    .Task);

        if (result.ExitCode != 0)
            throw new FlowlineException(ExitCode.BuildFailed, "Pack failed — check your solution source.");

        return packagePath;
    }

    private async Task ImportSolutionAsync(string packagePath, EnvironmentInfo targetEnv, string slnName, CancellationToken ct)
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
                        .Add("--async"))
                    .WithValidation(CommandResultValidation.None)
                    .WithToolExecutionLog(RuntimeOptions)
                    .ExecuteAsync(ct)
                    .Task);

        if (result.ExitCode != 0)
            throw new FlowlineException(ExitCode.BuildFailed, "Deploy failed — check the environment and your PAC login.");
    }

    // ── DTAP gate (static helpers, tested directly) ──────────────────────────

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
        var version = doc.Root
            ?.Element("SolutionManifest")
            ?.Element("Version")
            ?.Value;

        if (string.IsNullOrEmpty(version))
            throw new FlowlineException(ExitCode.ValidationFailed, "Solution version not set in Solution.xml — set a version before deploying.");

        return version;
    }
}
