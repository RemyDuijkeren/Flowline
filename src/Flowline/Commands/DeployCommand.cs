using System.ComponentModel;
using System.Xml.Linq;
using CliWrap;
using Flowline.Config;
using Flowline.Core;
using Flowline.Utils;
using Flowline.Validation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class DeployCommand(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions) : FlowlineCommand<DeployCommand.Settings>(console, runtimeOptions)
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
        [Description("Deploy the managed package")]
        [DefaultValue(false)]
        public bool Managed { get; set; } = false;

        [CommandOption("--skip-dtap-check")]
        [Description("Skip DTAP promotion checks")]
        [DefaultValue(false)]
        public bool SkipDtapCheck { get; set; } = false;
    }

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        await GitUtils.AssertRepoCleanAsync(settings.Verbose, cancellationToken);

        // Determine target URL
        var targetUrl = settings.Target.ToLowerInvariant() switch
        {
            "prod" => Config!.ProdUrl,
            "uat"  => Config!.UatUrl,
            "test" => Config!.TestUrl,
            "dev"  => Config!.DevUrl,
            _ => settings.Target
        };

        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            Console.Error($"Can't resolve '{settings.Target}' — provide an explicit URL or check your .flowline config.");
            return (int)ExitCode.ConfigInvalid;
        }

        // Resolve solution
        var sln = Config!.GetOrUpdateSolution(settings.Solution, settings.Managed, settings);
        if (sln == null)
        {
            Console.Error("Solution name is required — use --solution <name>.");
            return (int)ExitCode.ConfigInvalid;
        }

        // Validate target environment
        var targetEnv = await Console.Status().FlowlineSpinner().StartAsync(
            $"Checking [bold]{targetUrl}[/]...",
            _ => FlowlineValidator.Default.GetEnvironmentInfoByUrlAsync(targetUrl, settings, cancellationToken));
        if (targetEnv == null)
        {
            Console.Error("Target environment not found — check the URL or your PAC login.");
            return (int)ExitCode.ConnectionFailed;
        }

        Console.MarkupLine($"[green]Target: [bold]{targetEnv.DisplayName}[/] ({targetEnv.EnvironmentUrl})[/]");

        var existingSolution = await Console.Status().FlowlineSpinner().StartAsync(
            $"Checking [bold]{sln.Name}[/]...",
            _ => FlowlineValidator.Default.GetSolutionInfoAsync(targetUrl, sln.Name, includeManaged: true, settings, cancellationToken));

        if (existingSolution != null)
        {
            if (settings.Managed && !existingSolution.IsManaged)
            {
                Console.Error($"'{sln.Name}' is unmanaged in {targetEnv.DisplayName} — importing managed is irreversible. Remove the unmanaged solution first, or deploy unmanaged.");
                return (int)ExitCode.ValidationFailed;
            }
            if (!settings.Managed && existingSolution.IsManaged)
            {
                Console.Error($"'{sln.Name}' is managed in {targetEnv.DisplayName} — can't import unmanaged over managed. Deploy managed instead.");
                return (int)ExitCode.ValidationFailed;
            }
        }

        var slnFolder = Path.Combine(RootFolder, "solutions", sln.Name);

        var dtapDecision = ResolveDtapGate(Config!, targetUrl);

        if (dtapDecision.Outcome == DtapGateOutcome.DevBlock)
        {
            Console.Error("Dev is a development environment — use 'sync' to push changes there, not 'deploy'.");
            return (int)ExitCode.ValidationFailed;
        }

        if (dtapDecision.Outcome == DtapGateOutcome.Check)
        {
            string localVersion;
            try
            {
                localVersion = ReadLocalSolutionVersion(PackageFolder(slnFolder));
            }
            catch (FlowlineException ex)
            {
                Console.Error(ex.Message);
                return (int)ExitCode.ValidationFailed;
            }

            if (settings.SkipDtapCheck)
            {
                Console.Skip($"Skipping DTAP gate — '{sln.Name}' not verified in {dtapDecision.PredecessorLabel}.");
            }
            else
            {
                var predecessorInfo = await Console.Status().FlowlineSpinner().StartAsync(
                    $"Checking [bold]{sln.Name}[/] in {dtapDecision.PredecessorLabel}...",
                    _ => FlowlineValidator.Default.GetSolutionInfoAsync(dtapDecision.PredecessorUrl!, sln.Name, includeManaged: true, settings, cancellationToken));

                if (predecessorInfo == null)
                {
                    Console.Error($"'{sln.Name}' hasn't been deployed to {dtapDecision.PredecessorLabel} yet — promote there first, or use --skip-dtap-check.");
                    return (int)ExitCode.ValidationFailed;
                }

                if (predecessorInfo.VersionNumber == null
                    || !Version.TryParse(predecessorInfo.VersionNumber, out var predVer)
                    || !Version.TryParse(localVersion, out var localVer)
                    || predVer < localVer)
                {
                    Console.Error($"'{sln.Name}' in {dtapDecision.PredecessorLabel} is v{predecessorInfo.VersionNumber ?? "unknown"} — promote v{localVersion} there first, or use --skip-dtap-check.");
                    return (int)ExitCode.ValidationFailed;
                }
            }
        }

        var cdsprojPath = Path.Combine(PackageFolder(slnFolder), $"{PackageName}.cdsproj");
        if (!File.Exists(cdsprojPath))
        {
            Console.Error($"No solution found at '{cdsprojPath}' — run 'clone' first.");
            return (int)ExitCode.NotFound;
        }

        // Block if local changes haven't been synced — deploy packs from src/, not dist/
        var drift = DriftChecker.Check(slnFolder, PackageFolder(slnFolder), cancellationToken: cancellationToken)
            .Where(w => w.Category is DriftCategory.OnlyLocal or DriftCategory.PluginSizeMismatch)
            .ToList();
        if (drift.Count > 0)
        {
            Console.Error("Local changes not in Dataverse — deploy would revert them. Run 'sync' first, or use --force to skip.");
            foreach (var w in drift)
                Console.Warning(w.Category == DriftCategory.OnlyLocal
                    ? $"Only local: {w.RelativePath}"
                    : $"Plugin size mismatch: {w.RelativePath}");
            if (!settings.Force) return (int)ExitCode.ValidationFailed;
        }

        var artifactsFolder = Path.Combine(slnFolder, "artifacts");
        Directory.CreateDirectory(artifactsFolder);

        var packageType = settings.Managed ? "Managed" : "Unmanaged";
        var suffix = settings.Managed ? "_managed" : "_unmanaged";
        var packagePath = Path.Combine(artifactsFolder, $"{sln.Name}{suffix}.zip");

        var (cmdNamePack, prefixArgsPack, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);
        var packResult = await Console.Status().FlowlineSpinner().StartAsync(
            $"Packing [bold]{sln.Name}[/]...",
            _ => Cli.Wrap(cmdNamePack)
                    .WithArguments(args => args
                        .AddIfNotNull(prefixArgsPack)
                        .Add("solution")
                        .Add("pack")
                        .Add("--folder").Add(Path.Combine(PackageFolder(slnFolder), "src"))
                        .Add("--zipFile").Add(packagePath)
                        .Add("--packageType").Add(packageType))
                    .WithValidation(CommandResultValidation.None)
                    .WithToolExecutionLog(settings.Verbose)
                    .ExecuteAsync(cancellationToken)
                    .Task);

        if (packResult.ExitCode != 0)
        {
            Console.Error("Pack failed — check your solution source.");
            return (int)ExitCode.BuildFailed;
        }

        var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);
        var pacSolutionImportCmd = Cli.Wrap(cmdName)
            .WithArguments(args => args
                .AddIfNotNull(prefixArgs)
                .Add("solution")
                .Add("import")
                .Add("--path").Add(packagePath)
                .Add("--environment").Add(targetEnv.EnvironmentUrl!)
                .Add("--async"))
            .WithValidation(CommandResultValidation.None)
            .WithToolExecutionLog();

        var importResult = await Console.Status().FlowlineSpinner().StartAsync(
            $"Deploying [bold]{sln.Name}[/] to [bold]{targetEnv.DisplayName}[/]...",
            _ => pacSolutionImportCmd
                    .WithStandardOutputPipe(PipeTarget.ToDelegate(s => Console.MarkupLineInterpolated($"[dim]PAC: {s}[/]")))
                    .WithStandardErrorPipe(PipeTarget.ToDelegate(System.Console.Error.WriteLine))
                    .ExecuteAsync(cancellationToken)
                    .Task);

        if (importResult.ExitCode != 0)
        {
            Console.Error("Deploy failed — check the environment and your PAC login.");
            return (int)ExitCode.BuildFailed;
        }

        Console.Done("Deployed! Your solution is live.");

        return 0;
    }

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
