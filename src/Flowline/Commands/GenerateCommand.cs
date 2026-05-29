using System.ComponentModel;
using CliWrap;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Utils;
using Microsoft.PowerPlatform.Dataverse.Client;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class GenerateCommand(IAnsiConsole console, DataverseConnector dataverseConnector, FlowlineRuntimeOptions runtimeOptions)
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
    }

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Resolve solution
        var projectSln = Config!.GetOrUpdateSolution(settings.Solution, settings: settings);
        if (projectSln == null)
            throw new FlowlineException("Solution name is required — pass it as an argument or configure a single solution in .flowline.");

        // Resolve DEV URL (R13)
        var devUrl = Config.GetOrUpdateDevUrl(settings.DevUrl, settings);
        if (string.IsNullOrEmpty(devUrl))
            throw new FlowlineException("No DEV environment configured — run 'flowline provision' or pass --dev <URL>.");

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

        // Derive namespace if not yet set (R4)
        var slnFolder = Path.Combine(RootFolder, AllSolutionsFolderName, projectSln.Name);
        bool namespaceWasDerived = false;
        var modelNamespace = projectSln.Generate?.Namespace;
        if (string.IsNullOrEmpty(modelNamespace))
        {
            modelNamespace = NamespaceDeriver.Derive(slnFolder, projectSln.Name);
            namespaceWasDerived = true;
            Console.Verbose($"Derived namespace: [bold]{modelNamespace}[/]", settings.Verbose);
        }

        // Dirty-tree guard for Plugins/Models/
        var modelsFolder = Path.Combine(slnFolder, PluginsName, "Models");
        if (Directory.Exists(modelsFolder))
        {
            var modelsSummary = await SolutionChangeSummary.ComputeAsync(modelsFolder, RootFolder, settings.Verbose, cancellationToken);
            if (modelsSummary.TotalFiles > 0)
                Console.Warning($"Uncommitted changes in 'Plugins/Models/' will be overwritten.");
        }

        // Connect to DEV (reuse PAC token cache)
        var service = await ConnectToDataverseAsync(dataverseConnector, devUrl, cancellationToken);

        // Resolve solution ID from Dataverse (needed for component queries)
        var devEnv = await GetAndCheckEnvironmentInfoAsync(EnvironmentRole.Dev, devUrl, settings, cancellationToken);
        var (_, remoteSln) = await GetAndCheckSolutionAsync(projectSln.Name, devEnv.EnvironmentUrl!, cancellationToken: cancellationToken, settings: settings);

        // Discover entities and custom APIs in parallel (R10–R12)
        var entityTask = Console.Status().FlowlineSpinner().StartAsync(
            "Discovering solution entities...",
            _ => GenerateReader.GetSolutionEntityLogicalNamesAsync(service, remoteSln.Id, cancellationToken));
        var customApiTask = GenerateReader.GetSolutionCustomApiMessageNamesAsync(service, remoteSln.Id, cancellationToken);

        var solutionEntities = await entityTask;
        var customApiNames = await customApiTask;

        // Deduplicate entity filter (R14): solution entities + extraTables
        var extraTables = projectSln.Generate?.ExtraTables ?? [];
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

        // Temp folder for safe output (R17)
        var tempFolder = Path.Combine(slnFolder, PluginsName, "Models~");

        // Clean up any leftover temp folder from a previous killed run
        if (Directory.Exists(tempFolder))
            Directory.Delete(tempFolder, recursive: true);

        // Build pac command (R1, R2, R3, R10, R11)
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

        // Strip the absolute temp folder prefix from pac output lines so paths stay readable (R6)
        var tempPrefix = tempFolder + Path.DirectorySeparatorChar;
        string ShortenPacLine(string line) => line.Replace(tempPrefix, "Models/");

        // Run pac modelbuilder build; --verbose prints command + streams output (R6, R17)
        CommandResult result;
        try
        {
            result = await Console.Status().FlowlineSpinner().StartAsync(
                $"Generating early-bound types into [bold]{projectSln.Name}/Plugins/Models[/]...",
                ctx => pacCommand.WithToolExecutionLog(settings.Verbose, ctx, ShortenPacLine).ExecuteAsync(cancellationToken).Task);
        }
        catch
        {
            // Discard temp folder on any exception (including cancellation) — leave Plugins/Models/ unchanged (R17)
            if (Directory.Exists(tempFolder))
                Directory.Delete(tempFolder, recursive: true);
            throw;
        }

        if (!result.IsSuccess)
        {
            // Discard temp folder — leave Plugins/Models/ unchanged (R17)
            if (Directory.Exists(tempFolder))
                Directory.Delete(tempFolder, recursive: true);

            throw new FlowlineException("pac modelbuilder build failed — check the output above.");
        }

        Console.Ok("Early-bound types generated");

        // Swap: replace Plugins/Models/ with temp folder (R17)
        // Guard: verify pac produced output before touching the existing folder
        if (!Directory.Exists(tempFolder))
            throw new FlowlineException("pac modelbuilder build reported success but produced no output folder.");
        if (Directory.Exists(modelsFolder))
            Directory.Delete(modelsFolder, recursive: true);
        Directory.Move(tempFolder, modelsFolder);

        // Save derived namespace to .flowline after successful run (R5)
        if (namespaceWasDerived || settings.Namespace != null || settings.ExtraTables != null)
        {
            projectSln.Generate ??= new GenerateConfig();
            if (namespaceWasDerived)
                projectSln.Generate.Namespace = modelNamespace;
            Config.Save(RootFolder);
        }

        var duration = FormatDuration(result.RunTime);
        Console.Done($"Types generated into [bold]Plugins/Models[/] in {duration}. Namespace: [bold]{modelNamespace}[/]");

        return 0;
    }
}
