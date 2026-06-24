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
        [Description("Output folder for generated types — saved to .flowline (required outside a Flowline project)")]
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
                throw new FlowlineException(ExitCode.ValidationFailed, "Solution name is required — pass it as the first argument.");
            if (string.IsNullOrWhiteSpace(settings.DevUrl))
                throw new FlowlineException(ExitCode.ValidationFailed, "Dev URL is required in standalone mode — use --dev <URL>.");
            if (string.IsNullOrWhiteSpace(settings.Output))
                throw new FlowlineException(ExitCode.ValidationFailed, "Output folder is required in standalone mode — use -o <PATH> or --output <PATH>.");

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

            if (!string.IsNullOrWhiteSpace(settings.Output))
            {
                projectSln.Generate ??= new GenerateConfig();
                projectSln.Generate.OutputPath = Path.GetRelativePath(RootFolder, Path.GetFullPath(settings.Output));
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

            // Resolve output path: --output flag > saved OutputPath > convention
            var savedOutputPath = projectSln.Generate?.OutputPath;
            modelsFolder = !string.IsNullOrWhiteSpace(settings.Output)
                ? Path.GetFullPath(settings.Output)
                : !string.IsNullOrWhiteSpace(savedOutputPath)
                    ? Path.GetFullPath(Path.Combine(RootFolder, savedOutputPath))
                    : Path.Combine(slnFolder, PluginsName, "Models");

            extraTables = projectSln.Generate?.ExtraTables ?? [];

            // Dirty-tree guard
            if (Directory.Exists(modelsFolder))
            {
                var modelsSummary = await SolutionChangeSummary.ComputeAsync(modelsFolder, RootFolder, settings.Verbose, cancellationToken);
                if (modelsSummary.TotalFiles > 0)
                    Console.Warning($"'{Path.GetRelativePath(RootFolder, modelsFolder)}' has uncommitted changes — Flowline replaces generated files, preserves user files. Commit first to review the diff.");
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

        var outputLabel = standaloneMode
            ? Path.GetRelativePath(RootFolder, modelsFolder).Replace('\\', '/')
            : $"{solutionName}/Plugins/Models";

        XrmContextAuth? xrmContextAuth = null;
        string? resolvedSecret = null;

        if (resolvedGeneratorType is GeneratorType.XrmContext3)
        {
            if (effectiveProfile.IsUniversal)
            {
                if (!ConsoleHelper.IsInteractive(settings))
                    throw new FlowlineException(ExitCode.NotAuthenticated,
                        "xrmcontext3 uses ADAL browser OAuth for UNIVERSAL profiles — not available in non-interactive/CI mode. " +
                        "Pass --client-id <CLIENT_ID> --client-secret <SECRET> to authenticate as a service principal. " +
                        "Alternatively, upgrade to XrmContext v4 using --generator xrmcontext which produces a different output layout.");
                xrmContextAuth = new XrmContextAuth.BrowserOAuth(DataverseConnector.PacCliAppId);
            }
            else if (effectiveProfile.IsServicePrincipal)
            {
                if (string.IsNullOrEmpty(effectiveProfile.ApplicationId))
                    throw new FlowlineException(ExitCode.NotAuthenticated,
                        "Service principal profile is missing ApplicationId — pass --client-id <CLIENT_ID> --client-secret <SECRET> to supply credentials directly.");
                resolvedSecret = await secretResolver.ResolveAsync(effectiveProfile, settings.ClientSecret);
                xrmContextAuth = new XrmContextAuth.ClientSecret(effectiveProfile.ApplicationId, resolvedSecret);
            }
            else
            {
                throw new FlowlineException(ExitCode.NotAuthenticated,
                    $"PAC profile kind '{effectiveProfile.Kind}' is not supported by xrmcontext3 — switch to a service principal or UNIVERSAL profile, or pass --client-id <CLIENT_ID> --client-secret <SECRET>. " +
                    $"run: pac auth select");
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
            ?? throw new FlowlineException(ExitCode.GeneralError, $"Generator '{resolvedGeneratorType}' is not registered. This is a bug.");

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
        try
        {
            var generatorPaths = Directory.EnumerateFiles(tempFolder, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(tempFolder, f))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (generatorPaths.Count == 0)
                throw new FlowlineException(ExitCode.BuildFailed,
                    "Generator reported success but produced no output. Re-run with --verbose to see tool output.");

            if (Directory.Exists(modelsFolder))
            {

                foreach (var file in Directory.EnumerateFiles(modelsFolder, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(modelsFolder, file);
                    if (generatorPaths.Contains(rel) || IsGeneratorOwned(file)) continue;
                    var dest = Path.Combine(tempFolder, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest);
                }
                Directory.Delete(modelsFolder, recursive: true);
            }
            Directory.Move(tempFolder, modelsFolder);
        }
        catch
        {
            if (Directory.Exists(tempFolder))
                Directory.Delete(tempFolder, recursive: true);
            throw;
        }

        // Save to .flowline — project mode only, skipped when --output overrides the path
        if (!standaloneMode && projectSln != null && string.IsNullOrWhiteSpace(settings.Output))
        {
            projectSln.Generate ??= new GenerateConfig();
            if (namespaceWasDerived)
                projectSln.Generate.Namespace = modelNamespace;
            projectSln.Generate.Generator = resolvedGeneratorType;
            Config!.Save(RootFolder);
        }

        Console.Done($"Types generated into [bold]{outputLabel}[/] in {FormatDuration(sw.Elapsed)} ᕦ(ò_óˇ)ᕤ");

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

    private static bool IsGeneratorOwned(string filePath) =>
        File.ReadLines(filePath).Take(15).Any(line => line.Contains("<auto-generated>"));
}
