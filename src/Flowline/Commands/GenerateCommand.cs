using System.ComponentModel;
using CliWrap;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Services;
using Flowline.Utils;
using Flowline.Validation;
using Microsoft.PowerPlatform.Dataverse.Client;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class GenerateCommand(IAnsiConsole console, DataverseConnector dataverseConnector, FlowlineRuntimeOptions runtimeOptions,
    XrmContextToolProvider xrmContextToolProvider, XrmContextRunner xrmContextRunner)
    : FlowlineCommand<GenerateCommand.Settings>(console, runtimeOptions)
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "[solution]")]
        [Description("Solution to generate types for")]
        public string? Solution { get; set; }

        [CommandOption("--namespace <NS>")]
        [Description("Model namespace — saved to .flowline for future runs")]
        public string? Namespace { get; set; }

        [CommandOption("--extra-tables <TABLES>")]
        [Description("Comma-separated extra tables to include; replaces the saved list")]
        public string? ExtraTables { get; set; }

        [CommandOption("--dev <URL>")]
        [Description("Dev environment URL")]
        public string? DevUrl { get; set; }

        [CommandOption("-o|--output <PATH>")]
        [Description("Output folder for generated types (required outside a Flowline project)")]
        public string? Output { get; set; }

        [CommandOption("--generator")]
        [Description("Model builder generator to use (pac|xrmcontext), default: pac")]
        public GeneratorType? Generator { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!IsStandaloneMode())
            return await base.ExecuteAsync(context, settings, cancellationToken);

        InitializeRuntimeOptions(settings);
        await CheckSetupAsync(settings, cancellationToken);
        return await ExecuteFlowlineAsync(context, settings, cancellationToken);
    }

    protected override async Task CheckSetupAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (!IsStandaloneMode())
        {
            await base.CheckSetupAsync(settings, cancellationToken);
            return;
        }

        await Console.Status().FlowlineSpinner().StartAsync("Checking your setup...", async _ =>
        {
            await FlowlineValidator.Default.EnsurePacCliAsync(settings, cancellationToken);
        });

        Console.Ok("All good, let's go!");
    }

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var standaloneMode = IsStandaloneMode();

        // --- Resolve inputs ---
        string solutionName;
        string devUrl;
        string modelNamespace;
        string modelsFolder;
        string[] extraTables;
        bool namespaceWasDerived = false;
        ProjectSolution? projectSln = null;

        if (standaloneMode)
        {
            if (string.IsNullOrWhiteSpace(settings.Solution))
                throw new FlowlineException(ExitCode.ConfigInvalid, "Solution name is required — pass it as the first argument.");
            if (string.IsNullOrWhiteSpace(settings.DevUrl))
                throw new FlowlineException(ExitCode.ConfigInvalid, "Dev URL is required in standalone mode — use --dev <URL>.");
            if (string.IsNullOrWhiteSpace(settings.Output))
                throw new FlowlineException(ExitCode.ConfigInvalid, "Output folder is required in standalone mode — use -o <PATH> or --output <PATH>.");

            solutionName = settings.Solution.Trim();
            devUrl = settings.DevUrl.Trim();
            modelsFolder = Path.GetFullPath(settings.Output);
            extraTables = settings.ExtraTables?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
            modelNamespace = !string.IsNullOrWhiteSpace(settings.Namespace)
                ? settings.Namespace.Trim()
                : $"{solutionName}.Models";
        }
        else
        {
            projectSln = Config!.GetOrUpdateSolution(settings.Solution, settings: settings);
            if (projectSln == null)
                throw new FlowlineException(ExitCode.ConfigInvalid, "Solution name is required — pass it as an argument or configure a single solution in .flowline.");

            var resolvedDevUrl = Config!.GetOrUpdateDevUrl(settings.DevUrl, settings);
            if (string.IsNullOrEmpty(resolvedDevUrl))
                throw new FlowlineException(ExitCode.ConfigInvalid, "No DEV environment configured — run 'flowline provision' or pass --dev <URL>.");
            devUrl = resolvedDevUrl;

            solutionName = projectSln.Name;

            // Apply --namespace (R7)
            if (!string.IsNullOrWhiteSpace(settings.Namespace))
            {
                projectSln.Generate ??= new GenerateConfig();
                projectSln.Generate.Namespace = settings.Namespace.Trim();
            }

            // Apply --extra-tables (R8): replaces full list; empty value clears the list
            if (settings.ExtraTables != null)
            {
                projectSln.Generate ??= new GenerateConfig();
                var tables = settings.ExtraTables.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                projectSln.Generate.ExtraTables = tables.Length > 0 ? tables : null;
            }

            var slnFolder = Path.Combine(RootFolder, AllSolutionsFolderName, solutionName);

            // Derive namespace if not yet set (R4)
            modelNamespace = projectSln.Generate?.Namespace ?? string.Empty;
            if (string.IsNullOrEmpty(modelNamespace))
            {
                modelNamespace = NamespaceDeriver.Derive(slnFolder, solutionName);
                namespaceWasDerived = true;
                Console.Verbose($"Derived namespace: [bold]{modelNamespace}[/]", settings.Verbose);
            }

            // Resolve output path — --output overrides convention, not saved to .flowline
            modelsFolder = !string.IsNullOrWhiteSpace(settings.Output)
                ? Path.GetFullPath(settings.Output)
                : Path.Combine(slnFolder, PluginsName, "Models");

            extraTables = projectSln.Generate?.ExtraTables ?? [];

            // Dirty-tree guard
            if (Directory.Exists(modelsFolder))
            {
                var modelsSummary = await SolutionChangeSummary.ComputeAsync(modelsFolder, RootFolder, settings.Verbose, cancellationToken);
                if (modelsSummary.TotalFiles > 0)
                    Console.Warning($"Uncommitted changes in '{Path.GetRelativePath(RootFolder, modelsFolder)}' will be overwritten.");
            }
        }

        var generator = settings.Generator ?? projectSln?.Generate?.Generator ?? GeneratorType.Pac;

        // --- Connect and validate ---
        var service = await ConnectToDataverseAsync(dataverseConnector, devUrl, cancellationToken);

        SolutionInfo remoteSln;
        if (standaloneMode)
        {
            await CheckStandaloneEnvironmentAsync(devUrl, settings, cancellationToken);
            remoteSln = await GetStandaloneSolutionAsync(solutionName, devUrl, settings, cancellationToken);
        }
        else
        {
            var devEnv = await GetAndCheckEnvironmentInfoAsync(EnvironmentRole.Dev, devUrl, settings, cancellationToken);
            (_, remoteSln) = await GetAndCheckSolutionAsync(solutionName, devEnv.EnvironmentUrl!, cancellationToken: cancellationToken, settings: settings);
        }

        var tempFolder = modelsFolder + "~";

        if (Directory.Exists(tempFolder))
            Directory.Delete(tempFolder, recursive: true);

        var outputLabel = standaloneMode ? modelsFolder : $"{solutionName}/Plugins/Models";

        if (generator == GeneratorType.XrmContext)
        {
            var exePath = await xrmContextToolProvider.GetExePathAsync(cancellationToken);

            string connectionString;
            try { connectionString = dataverseConnector.BuildXrmContextConnectionString(devUrl); }
            catch (InvalidOperationException ex) { throw new FlowlineException(ExitCode.NotAuthenticated, ex.Message); }

            try
            {
                await xrmContextRunner.RunAsync(
                    exePath: exePath,
                    solutionName: solutionName,
                    extraTables: extraTables.Length > 0 ? extraTables : null,
                    modelNamespace: modelNamespace,
                    connectionString: connectionString,
                    tempOutputPath: tempFolder,
                    cancellationToken: cancellationToken);
            }
            catch
            {
                if (Directory.Exists(tempFolder))
                    Directory.Delete(tempFolder, recursive: true);
                throw;
            }

            if (!Directory.Exists(tempFolder))
                throw new FlowlineException(ExitCode.BuildFailed, "XrmContext reported success but produced no output folder.");

            if (Directory.Exists(modelsFolder))
                Directory.Delete(modelsFolder, recursive: true);
            Directory.Move(tempFolder, modelsFolder);

            // Save to .flowline — project mode only, skipped when --output overrides the path
            if (!standaloneMode && projectSln != null && string.IsNullOrWhiteSpace(settings.Output))
            {
                projectSln.Generate ??= new GenerateConfig();
                if (namespaceWasDerived)
                    projectSln.Generate.Namespace = modelNamespace;
                projectSln.Generate.Generator = generator;
                Config!.Save(RootFolder);
            }

            Console.Done($"Types generated into [bold]{outputLabel}[/]. Namespace: [bold]{modelNamespace}[/]");
        }
        else
        {
            // --- Discover entities and custom APIs in parallel (R10–R12) ---
            var entityTask = Console.Status().FlowlineSpinner().StartAsync(
                "Discovering solution entities...",
                _ => GenerateReader.GetSolutionEntityLogicalNamesAsync(service, remoteSln.Id, cancellationToken));
            var customApiTask = GenerateReader.GetSolutionCustomApiMessageNamesAsync(service, remoteSln.Id, cancellationToken);

            var solutionEntities = await entityTask;
            var customApiNames = await customApiTask;

            // Deduplicate entity filter (R14): solution entities + extraTables
            var entityFilter = solutionEntities
                .Concat(extraTables)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Console.Ok($"Found [bold]{entityFilter.Count}[/] entities" +
                       (customApiNames.Count > 0 ? $", [bold]{customApiNames.Count}[/] custom APIs" : ""));

            if (settings.Verbose)
            {
                foreach (var entity in entityFilter)
                    Console.Verbose($"  entity: {entity}", isVerbose: true);
                foreach (var api in customApiNames)
                    Console.Verbose($"  custom api: {api}", isVerbose: true);
            }

            // --- Build and run pac modelbuilder build ---
            var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);

            var pacCommand = Cli.Wrap(cmdName)
                .WithArguments(args =>
                {
                    args.AddIfNotNull(prefixArgs)
                        .Add("modelbuilder").Add("build")
                        .Add("-o").Add(tempFolder)
                        .Add("-enf").Add(string.Join(";", entityFilter))
                        .Add("-sgca")
                        .Add("--suppressINotifyPattern")
                        .Add("--emitfieldsclasses")
                        .Add("-n").Add(modelNamespace);

                    if (customApiNames.Count > 0)
                    {
                        args.Add("--generatesdkmessages")
                            .Add("--messagenamesfilter").Add(string.Join(";", customApiNames));
                    }
                })
                .WithValidation(CommandResultValidation.None);

            var tempPrefix = tempFolder + Path.DirectorySeparatorChar;
            var outputFolderName = Path.GetFileName(modelsFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string ShortenPacLine(string line) => line.Replace(tempPrefix, outputFolderName + "/");

            CommandResult result;
            try
            {
                result = await Console.Status().FlowlineSpinner().StartAsync(
                    $"Generating early-bound types into [bold]{outputLabel}[/]...",
                    ctx => pacCommand.WithToolExecutionLog(settings.Verbose, ctx, ShortenPacLine).ExecuteAsync(cancellationToken).Task);
            }
            catch
            {
                if (Directory.Exists(tempFolder))
                    Directory.Delete(tempFolder, recursive: true);
                throw;
            }

            if (!result.IsSuccess)
            {
                if (Directory.Exists(tempFolder))
                    Directory.Delete(tempFolder, recursive: true);

                throw new FlowlineException(ExitCode.BuildFailed, "pac modelbuilder build failed — check the output above.");
            }

            Console.Ok("Early-bound types generated");

            if (!Directory.Exists(tempFolder))
                throw new FlowlineException(ExitCode.BuildFailed, "pac modelbuilder build reported success but produced no output folder.");
            if (Directory.Exists(modelsFolder))
                Directory.Delete(modelsFolder, recursive: true);
            Directory.Move(tempFolder, modelsFolder);

            // Save to .flowline — project mode only, skipped when --output overrides the path (R5)
            if (!standaloneMode && projectSln != null && string.IsNullOrWhiteSpace(settings.Output))
            {
                projectSln.Generate ??= new GenerateConfig();
                if (namespaceWasDerived)
                    projectSln.Generate.Namespace = modelNamespace;
                projectSln.Generate.Generator = generator;
                Config!.Save(RootFolder);
            }

            var duration = FormatDuration(result.RunTime);
            Console.Done($"Types generated into [bold]{outputLabel}[/] in {duration}. Namespace: [bold]{modelNamespace}[/]");
        }

        return 0;
    }

    private bool IsStandaloneMode() =>
        !File.Exists(Path.Combine(RootFolder, ProjectConfig.s_configFileName));

    private async Task CheckStandaloneEnvironmentAsync(string devUrl, Settings settings, CancellationToken cancellationToken)
    {
        EnvironmentInfo? env = await Console.Status().FlowlineSpinner().StartAsync(
            $"Checking dev [bold]{devUrl}[/]...",
            _ => FlowlineValidator.Default.GetEnvironmentInfoByUrlAsync(devUrl, settings, cancellationToken));

        if (env == null)
            throw new FlowlineException(ExitCode.ConnectionFailed, "Dev environment not found — check the URL or your PAC login.");
        if (env.Type == "Production")
            throw new FlowlineException(ExitCode.ValidationFailed, "That's a Production environment — use a sandbox or dev instead.");

        Console.Ok($"Dev: [bold]{env.DisplayName}[/] ({env.EnvironmentUrl})");
    }

    private async Task<SolutionInfo> GetStandaloneSolutionAsync(string solutionName, string environmentUrl, Settings settings, CancellationToken cancellationToken)
    {
        SolutionInfo? remoteSln = await Console.Status().FlowlineSpinner().StartAsync(
            $"Looking up [bold]{solutionName}[/]...",
            _ => FlowlineValidator.Default.GetSolutionInfoAsync(environmentUrl, solutionName, includeManaged: false, settings, cancellationToken));

        if (remoteSln == null)
            throw new FlowlineException(ExitCode.NotFound, $"Solution '{solutionName}' not found in that environment.");

        Console.Ok($"Solution: [bold]{solutionName}[/] (managed: {remoteSln.IsManaged})");
        return remoteSln;
    }
}
