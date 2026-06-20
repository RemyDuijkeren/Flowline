using System.ComponentModel;
using System.Diagnostics;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Generators;
using Flowline.Services;
using Flowline.Utils;
using Flowline.Validation;
using Microsoft.PowerPlatform.Dataverse.Client;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class GenerateCommand(IAnsiConsole console, DataverseConnector dataverseConnector, FlowlineRuntimeOptions runtimeOptions,
    IEnumerable<IGenerator> generators, ProfileResolutionService profileResolutionService, SecretResolver secretResolver)
    : FlowlineCommand<GenerateCommand.Settings>(console, runtimeOptions, profileResolutionService)
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "[solution]")]
        [Description("Solution to generate types for")]
        public string? Solution { get; set; }

        [CommandOption("--namespace <NS>")]
        [Description("Model namespace — saved to .flowline for future runs")]
        public string? Namespace { get; set; }

        [CommandOption("--service-context-name <NAME>")]
        [Description("Name of the generated OrganizationServiceContext class (default: XrmContext) — saved to .flowline")]
        public string? ServiceContextName { get; set; }

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
        [Description("Model builder generator to use (pac|xrmcontext3|xrmcontext), default: pac")]
        public GeneratorType? Generator { get; set; }

        [CommandOption("--client-id <ID>")]
        [Description("Override client ID for generator subprocess (XrmContext/XrmContext3 only)")]
        public string? ClientId { get; set; }

        [CommandOption("--client-secret <SECRET>")]
        [Description("Client secret for generator subprocess (XrmContext/XrmContext3 only)")]
        public string? ClientSecret { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (settings.ClientId != null && string.IsNullOrWhiteSpace(settings.ClientId))
            throw new FlowlineException(ExitCode.ValidationFailed, "--client-id must not be empty");

        if (settings.ClientId != null && settings.ClientSecret == null)
            throw new FlowlineException(ExitCode.ValidationFailed, "--client-id requires --client-secret");

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

            if (!string.IsNullOrWhiteSpace(settings.ServiceContextName))
            {
                projectSln.Generate ??= new GenerateConfig();
                projectSln.Generate.ServiceContextName = settings.ServiceContextName.Trim();
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
                    Console.Warning($"'{Path.GetRelativePath(RootFolder, modelsFolder)}' has uncommitted changes — they will be overwritten. Commit them first if you want to review what changed.");
            }
        }

        var resolvedGeneratorType = settings.Generator ?? projectSln?.Generate?.Generator ?? GeneratorType.Pac;
        var serviceContextName = settings.ServiceContextName ?? projectSln?.Generate?.ServiceContextName;

        // --- Connect and validate ---
        var (service, resolvedProfile) = await ConnectToDataverseAsync(dataverseConnector, devUrl, cancellationToken);

        // Guard: --client-secret with UNIVERSAL profile requires --client-id override
        if (settings.ClientSecret != null && resolvedProfile.IsUniversal && settings.ClientId == null)
            throw new FlowlineException(ExitCode.ValidationFailed,
                "--client-secret requires a service principal profile or --client-id override");

        // Apply --client-id override: substitutes ApplicationId and promotes to SP kind so auth routing works
        var effectiveProfile = settings.ClientId != null
            ? resolvedProfile with { ApplicationId = settings.ClientId, Kind = "ServicePrincipal" }
            : resolvedProfile;

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

        XrmContextAuth? xrmContextAuth = null;
        string? resolvedSecret = null;

        if (resolvedGeneratorType is GeneratorType.XrmContext3)
        {
            if (effectiveProfile.IsUniversal)
            {
                if (!ConsoleHelper.IsInteractive(settings))
                    throw new FlowlineException(ExitCode.ConfigInvalid,
                        "XrmContext3 requires browser OAuth which is not available in non-interactive mode. Use --generator xrmcontext (XrmContext v4) for non-interactive use.");
                xrmContextAuth = new XrmContextAuth.BrowserOAuth(DataverseConnector.PacCliAppId);
            }
            else if (effectiveProfile.IsServicePrincipal)
            {
                if (string.IsNullOrEmpty(effectiveProfile.ApplicationId))
                    throw new FlowlineException(ExitCode.ConfigInvalid, "Service principal profile is missing ApplicationId.");
                resolvedSecret = await secretResolver.ResolveAsync(effectiveProfile, settings.ClientSecret);
                xrmContextAuth = new XrmContextAuth.ClientSecret(effectiveProfile.ApplicationId, resolvedSecret);
            }
            else
            {
                throw new FlowlineException(ExitCode.ConfigInvalid,
                    $"PAC profile kind '{effectiveProfile.Kind}' is not supported by XrmContext3. Use a UNIVERSAL or service principal profile, or switch to --generator pac.");
            }
        }
        else if (resolvedGeneratorType is GeneratorType.XrmContext && effectiveProfile.IsServicePrincipal)
        {
            // XrmContext v4 SP: resolve secret for env injection (U6 uses context.ResolvedSecret)
            resolvedSecret = await secretResolver.ResolveAsync(effectiveProfile, settings.ClientSecret);
        }

        // --- Dispatch generator ---
        var generationContext = new GenerationContext(
            Service: service,
            RemoteSolution: remoteSln,
            SolutionName: solutionName,
            DevUrl: devUrl,
            ModelNamespace: modelNamespace,
            ExtraTables: extraTables,
            TempOutputPath: tempFolder,
            XrmContextAuth: xrmContextAuth,
            Verbose: settings.Verbose,
            OutputLabel: outputLabel,
            ServiceContextName: serviceContextName,
            ResolvedProfile: effectiveProfile,
            ResolvedSecret: resolvedSecret
        );

        var generator = generators.SingleOrDefault(g => g.Type == resolvedGeneratorType)
            ?? throw new FlowlineException(ExitCode.ConfigInvalid, $"Generator '{resolvedGeneratorType}' is not registered. This is a bug.");

        var sw = Stopwatch.StartNew();
        try
        {
            await generator.RunAsync(generationContext, cancellationToken);
        }
        catch
        {
            if (Directory.Exists(tempFolder))
                Directory.Delete(tempFolder, recursive: true);
            throw;
        }
        sw.Stop();

        // --- Shared tail (all generators) ---
        if (!Directory.EnumerateFiles(tempFolder, "*", SearchOption.AllDirectories).Any())
            throw new FlowlineException(ExitCode.BuildFailed,
                "Generator reported success but produced no output. Re-run with --verbose to see tool output.");

        if (Directory.Exists(modelsFolder))
            Directory.Delete(modelsFolder, recursive: true);
        Directory.Move(tempFolder, modelsFolder);

        // Save to .flowline — project mode only, skipped when --output overrides the path
        if (!standaloneMode && projectSln != null && string.IsNullOrWhiteSpace(settings.Output))
        {
            projectSln.Generate ??= new GenerateConfig();
            if (namespaceWasDerived)
                projectSln.Generate.Namespace = modelNamespace;
            projectSln.Generate.Generator = resolvedGeneratorType;
            Config!.Save(RootFolder);
        }

        Console.Done($"Types generated into [bold]{outputLabel}[/] in {FormatDuration(sw.Elapsed)}. Namespace: [bold]{modelNamespace}[/]");

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
