using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Generators;
using Flowline.Infrastructure;
using Flowline.Services;
using Flowline.Utils;
using Flowline.Validation;
using Microsoft.PowerPlatform.Dataverse.Client;
using Spectre.Console;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class GenerateCommand(IAnsiConsole console, DataverseConnector dataverseConnector, FlowlineRuntimeOptions runtimeOptions,
    IEnumerable<IGenerator> generators, ProfileResolutionService profileResolutionService, SecretResolver secretResolver, ILoggerFactory loggerFactory, SubprocessCapture capture, NuGetVersionClient nuGetVersionClient, EnvironmentTargetResolver environmentTargetResolver)
    : FlowlineCommand<GenerateCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture, nuGetVersionClient)
{
    public sealed class Settings : EnvironmentSettings
    {
        [CommandArgument(0, "[solution]")]
        [Description("Solution to generate types for (optional in project mode)")]
        public string? Solution { get; set; }

        [CommandOption("--namespace <NS>")]
        [Description("Model namespace (saved to .flowline)")]
        public string? Namespace { get; set; }

        [CommandOption("--service-context-name <NAME>")]
        [Description("Name of the generated OrganizationServiceContext class, default: XrmContext (saved to .flowline)")]
        public string? ServiceContextName { get; set; }

        [CommandOption("--extra-tables <TABLES>")]
        [Description("Comma-separated extra tables to include; replaces the saved list (saved to .flowline)")]
        public string? ExtraTables { get; set; }

        [CommandOption("-o|--output <PATH>")]
        [Description("Output folder for generated types, required outside a Flowline project (saved to .flowline)")]
        public string? Output { get; set; }

        [CommandOption("--generator")]
        [Description("Model builder generator to use (pac|xrmcontext3|xrmcontext|ebg), default: pac (saved to .flowline)")]
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

        // Both modes go through the base pipeline from here — this override exists only for the two
        // argument checks above, which have to run before anything else.
        return await base.ExecuteAsync(context, settings, cancellationToken);
    }

    // Standalone runs through the base pipeline like every other mode, rather than around it: the base
    // class owns root resolution, the pac-only setup, the welcome screen, invocation logging, the
    // activity span, and --force validation.
    //
    // Reads RootFolder, which is why the base asks this before its own walk-up: at that point RootFolder
    // is still the working directory, so this keeps its original "is there a .flowline right here"
    // meaning rather than widening to "is there one in any ancestor".
    protected override bool IsStandalone(Settings settings) => IsStandaloneMode();

    protected override string[] ValidForceSpecifiers => FlowlineSettings.ConfigOnlyValidSpecifiers;

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
        EnvironmentRole? resolvedRole = null;

        if (standaloneMode)
        {
            if (string.IsNullOrWhiteSpace(settings.Solution))
                throw new FlowlineException(ExitCode.ValidationFailed, "Solution name is required — pass it as the first argument.");
            if (string.IsNullOrWhiteSpace(settings.Output))
                throw new FlowlineException(ExitCode.ValidationFailed, "Output folder is required in standalone mode — use -o <PATH> or --output <PATH>.");

            solutionName = settings.Solution.Trim();
            EnvironmentTargetResolver.EnsureUsableStandaloneEnv(settings.Env);
            devUrl = ProfileResolutionService.ResolveStandaloneEnvironmentUrl(settings.Env);
            modelsFolder = Path.GetFullPath(settings.Output);
            extraTables = settings.ExtraTables?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
            modelNamespace = !string.IsNullOrWhiteSpace(settings.Namespace)
                ? settings.Namespace.Trim()
                : $"{solutionName}.Models";
        }
        else
        {
            // R8: a passed [solution] no longer selects among multiple configured solutions (only one
            // exists) — it now just needs to match the one already configured.
            if (Config!.Solution != null)
                ValidateSolutionMatchesConfig(settings.Solution, Config.Solution.UniqueName);

            projectSln = Config!.GetOrUpdateSolution(settings.Solution, settings: settings);
            if (projectSln == null)
                throw new FlowlineException(ExitCode.ConfigInvalid, "Solution name is required — pass it as an argument or configure a single solution in .flowline.");

            // KTD8: generate accepts any role (incl. Production) — devOnly:false, and the type guard is
            // skipped below when the environment is actually checked. Any URL the resolver saves lands
            // in Config in-memory here; the existing end-of-run Config!.Save(RootFolder) (ShouldPersistSettings,
            // below) is what actually flushes it once generation succeeds — matching how projectSln's own
            // mutations above are already deferred to that one save.
            var target = await environmentTargetResolver.ResolveAsync(settings.Env, Config!, devOnly: false, IsInteractive(), settings,
                (url, ct) => Validator.GetEnvironmentInfoByUrlAsync(url, settings, settings.NoCache, ct), cancellationToken);
            devUrl = target.Url;
            resolvedRole = target.Role;

            solutionName = projectSln.UniqueName;

            // Apply --namespace, --extra-tables, --generator, --output, --service-context-name (R7, R8,
            // R11): each one write-on-first-use, ask-on-change against .flowline, via the same core the
            // environment URLs use.
            ApplyPersistedGenerateSettings(projectSln, settings, RootFolder);

            var slnFolder = RootFolder;

            // The one plugin project the namespace and the default output folder both key off, resolved
            // once so they can't disagree about which project the models belong to.
            var primaryPluginProject = await NamespaceDeriver.ResolvePrimaryProjectAsync(slnFolder, cancellationToken);

            // Derive namespace if not yet set (R4)
            modelNamespace = projectSln.Generate?.Namespace ?? string.Empty;
            if (string.IsNullOrEmpty(modelNamespace))
            {
                modelNamespace = await NamespaceDeriver.DeriveAsync(slnFolder, solutionName, cancellationToken);
                namespaceWasDerived = true;
                Console.Verbose($"Derived namespace: {modelNamespace}");
            }

            // Resolve output path: --output flag > saved OutputPath > beside the plugin project (Models/).
            // The default follows the resolved project, so a relocated Plugins/ takes its Models/ with it
            // instead of stranding them in a composed Plugins/Models/ that nothing compiles. Only when no
            // plugin project is on disk does it fall back to the conventional Plugins/Models.
            var savedOutputPath = projectSln.Generate?.OutputPath;
            modelsFolder = !string.IsNullOrWhiteSpace(savedOutputPath)
                ? Path.GetFullPath(Path.Combine(RootFolder, savedOutputPath))
                : !string.IsNullOrWhiteSpace(settings.Output)
                    ? Path.GetFullPath(settings.Output)
                    : primaryPluginProject != null
                        ? Path.Combine(Path.GetDirectoryName(primaryPluginProject)!, "Models")
                        : Path.Combine(slnFolder, "Plugins", "Models");

            extraTables = projectSln.Generate?.ExtraTables ?? [];

            // Dirty-tree guard
            if (Directory.Exists(modelsFolder))
            {
                var modelsSummary = await SolutionChangeSummary.ComputeAsync(modelsFolder, RootFolder, _capture, cancellationToken);
                if (modelsSummary.TotalFiles > 0)
                    Console.Warning($"'{Path.GetRelativePath(RootFolder, modelsFolder)}' has uncommitted changes — Flowline replaces generated files, preserves user files. Commit first to review the diff.");
            }
        }

        // Config-first: ApplyPersistedGenerateSettings already merged the passed flag into projectSln.Generate
        // when it was accepted (or the value already matched); reading config first, not the raw flag,
        // is what makes a declined overwrite prompt actually stick for the rest of this run. settings.X
        // only matters here in standalone mode, where projectSln is null and there's no config to read.
        var resolvedGeneratorType = projectSln?.Generate?.Generator ?? settings.Generator ?? GeneratorType.Pac;
        var serviceContextName = projectSln?.Generate?.ServiceContextName ?? settings.ServiceContextName;
        Logger.LogInformation("solution={SolutionName} devUrl={DevUrl} generator={Generator} output={Output}", solutionName, devUrl, resolvedGeneratorType, modelsFolder);

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
            await GetAndCheckStandaloneEnvironmentAsync(devUrl, settings, cancellationToken, resolvedProfile);
            remoteSln = await GetAndCheckStandaloneSolutionAsync(solutionName, devUrl, settings, cancellationToken);
        }
        else
        {
            // KTD8: skipTypeGuard — generate reads types, never mutates, so it accepts any role incl. Production.
            var (devEnv, _) = await GetAndCheckEnvironmentAsync(devUrl, resolvedRole, settings, cancellationToken, resolvedProfile, skipTypeGuard: true);
            (_, remoteSln) = await GetAndCheckSolutionAsync(solutionName, devEnv.EnvironmentUrl!, cancellationToken: cancellationToken, settings: settings);
        }

        var tempFolder = modelsFolder + "~";

        if (Directory.Exists(tempFolder))
            Directory.Delete(tempFolder, recursive: true);

        var outputLabel = Path.GetRelativePath(RootFolder, modelsFolder).Replace('\\', '/');

        var (xrmContextAuth, resolvedSecret) = await ResolveXrmContextAuthAsync(resolvedGeneratorType, effectiveProfile, settings, cancellationToken);

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
            ResolvedSecret: resolvedSecret,
            BuilderSettingsPath: Path.Combine(RootFolder, "builderSettings.json")
        );

        var generator = generators.SingleOrDefault(g => g.Type == resolvedGeneratorType)
            ?? throw new FlowlineException(ExitCode.GeneralError, $"Generator '{resolvedGeneratorType}' is not registered. This is a bug.");

        Logger.LogInformation("Running generator: {Generator}", generator.Type);
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
        Logger.LogInformation("Generator ran in {Duration}", FormatDuration(sw.Elapsed));

        // --- Shared tail (all generators) ---
        ReplaceModelsFolderWithGenerated(tempFolder, modelsFolder);

        // Save to .flowline — project mode only. The five flags applied by ApplyPersistedGenerateSettings
        // already sit on projectSln.Generate; a derived namespace (no --namespace, nothing saved yet)
        // isn't a flag R11 governs, so it's still assigned directly and silently.
        if (ShouldPersistSettings(standaloneMode, projectSln))
        {
            if (namespaceWasDerived)
            {
                projectSln.Generate ??= new GenerateConfig();
                projectSln.Generate.Namespace = modelNamespace;
            }
            Config!.Save(RootFolder);
        }

        Console.Done($"Types generated into [bold]{outputLabel}[/] in {FormatDuration(sw.Elapsed)} ᕦ(ò_óˇ)ᕤ");

        return 0;
    }

    private bool IsStandaloneMode() =>
        !File.Exists(Path.Combine(RootFolder, ProjectConfig.s_configFileName));


    /// <summary>
    /// Whether a completed run writes what it resolved back to <c>.flowline</c>: project mode with a
    /// configured solution.
    /// </summary>
    /// <remarks>
    /// <c>--output</c> deliberately doesn't gate this. It is one of the settings being saved, so skipping
    /// the save when it was passed would discard the very path it names, and take that run's generator and
    /// derived namespace down with it.
    /// </remarks>
    internal static bool ShouldPersistSettings(bool standaloneMode, [NotNullWhen(true)] ProjectSolution? projectSln) =>
        !standaloneMode && projectSln != null;

    /// <summary>
    /// Applies <c>--namespace</c>, <c>--extra-tables</c>, <c>--generator</c>, <c>--output</c> and
    /// <c>--service-context-name</c> to <paramref name="projectSln"/>.Generate — project mode only.
    /// Each one routes through <see cref="ProjectConfig.GetOrUpdateValue"/> (R11): a flag left off keeps
    /// the saved value untouched; passed against an unset key it saves and prints; passed with the saved
    /// value it's silent; passed with a different value asks to overwrite, or exits non-interactively
    /// without <c>--force config</c>. A flag that's absent isn't routed through the core at all — only
    /// its own guard below decides that — so an ordinary run stays free of the core's verbose echo.
    /// </summary>
    /// <remarks>
    /// <c>--extra-tables</c> loses its old "empty value clears the list" escape hatch: an empty value now
    /// reads the same as an absent flag under the shared rule. Clearing means editing the list in
    /// <c>.flowline</c> directly, or passing a different, non-empty list through the overwrite prompt.
    /// <para>
    /// Declining an overwrite prompt only sticks for the rest of the run because every downstream read
    /// of these five values comes from <c>projectSln.Generate</c>, never straight from <paramref
    /// name="settings"/> — see <c>resolvedGeneratorType</c>/<c>serviceContextName</c>/<c>modelsFolder</c>
    /// in <c>ExecuteFlowlineAsync</c>. Reintroducing a <c>settings.X ??</c> fallback ahead of the config
    /// read on any of those would silently let a declined flag win anyway.
    /// </para>
    /// </remarks>
    internal static void ApplyPersistedGenerateSettings(ProjectSolution projectSln, Settings settings, string rootFolder)
    {
        if (!string.IsNullOrWhiteSpace(settings.Namespace))
            SetGenerate(projectSln, settings.Namespace.Trim(), g => g.Namespace, (g, v) => g.Namespace = v,
                "Namespace", "Solution.Generate.Namespace", settings);

        if (settings.ExtraTables != null)
        {
            var tables = settings.ExtraTables.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var joined = tables.Length > 0 ? string.Join(',', tables) : null;
            SetGenerate(projectSln, joined,
                g => g.ExtraTables is { Length: > 0 } et ? string.Join(',', et) : null,
                (g, v) => g.ExtraTables = string.IsNullOrWhiteSpace(v) ? null : v.Split(','),
                "Extra tables", "Solution.Generate.ExtraTables", settings);
        }

        if (settings.Generator.HasValue)
            SetGenerate(projectSln, settings.Generator.Value.ToString(), g => g.Generator?.ToString(),
                (g, v) => g.Generator = Enum.Parse<GeneratorType>(v!),
                "Generator", "Solution.Generate.Generator", settings);

        if (!string.IsNullOrWhiteSpace(settings.ServiceContextName))
            SetGenerate(projectSln, settings.ServiceContextName.Trim(), g => g.ServiceContextName, (g, v) => g.ServiceContextName = v,
                "Service context name", "Solution.Generate.ServiceContextName", settings);

        if (!string.IsNullOrWhiteSpace(settings.Output))
            SetGenerate(projectSln, Path.GetRelativePath(rootFolder, Path.GetFullPath(settings.Output)), g => g.OutputPath, (g, v) => g.OutputPath = v,
                "Output path", "Solution.Generate.OutputPath", settings);
    }

    static void SetGenerate(ProjectSolution projectSln, string? input, Func<GenerateConfig, string?> get, Action<GenerateConfig, string?> set,
        string label, string key, FlowlineSettings settings)
    {
        projectSln.Generate ??= new GenerateConfig();
        var generate = projectSln.Generate;
        ProjectConfig.GetOrUpdateValue(input, () => get(generate), v => set(generate, v), label, key, settings);
    }

    async Task<(XrmContextAuth? XrmContextAuth, string? ResolvedSecret)> ResolveXrmContextAuthAsync(
        GeneratorType generatorType, PacProfile effectiveProfile, Settings settings, CancellationToken cancellationToken)
    {
        XrmContextAuth? xrmContextAuth = null;
        string? resolvedSecret = null;

        if (generatorType is GeneratorType.XrmContext3)
        {
            if (effectiveProfile.IsUniversal)
            {
                if (!Console.Profile.Capabilities.Interactive)
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
                resolvedSecret = await secretResolver.ResolveAsync(effectiveProfile, settings.ClientSecret, cancellationToken);
                xrmContextAuth = new XrmContextAuth.ClientSecret(effectiveProfile.ApplicationId, resolvedSecret);
            }
            else
            {
                throw new FlowlineException(ExitCode.NotAuthenticated,
                    $"PAC auth profile kind '{effectiveProfile.Kind}' is not supported by xrmcontext3 — switch to a service principal or UNIVERSAL profile, or pass --client-id <CLIENT_ID> --client-secret <SECRET>. " +
                    $"run: pac auth select");
            }
        }
        else if (generatorType is GeneratorType.XrmContext && effectiveProfile.IsServicePrincipal)
        {
            // XrmContext v4 SP: resolve secret for env injection (U6 uses context.ResolvedSecret)
            resolvedSecret = await secretResolver.ResolveAsync(effectiveProfile, settings.ClientSecret, cancellationToken);
        }

        return (xrmContextAuth, resolvedSecret);
    }

    // Moves the generator's temp output into place as the real models folder: preserves any
    // non-generator-owned user file already there, deletes now-empty leftover directories, then swaps
    // tempFolder in. Shared by every generator — XrmContext3, XrmContext v4, and Pac all land here the
    // same way once RunAsync returns.
    void ReplaceModelsFolderWithGenerated(string tempFolder, string modelsFolder)
    {
        try
        {
            var generatorPaths = Directory.EnumerateFiles(tempFolder, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(tempFolder, f))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Logger.LogInformation("Generator output: {FileCount} files", generatorPaths.Count);

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
            foreach (var dir in Directory.EnumerateDirectories(tempFolder, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            Directory.Move(tempFolder, modelsFolder);
        }
        catch
        {
            if (Directory.Exists(tempFolder))
                Directory.Delete(tempFolder, recursive: true);
            throw;
        }
    }

    private static bool IsGeneratorOwned(string filePath) =>
        File.ReadLines(filePath).Take(15).Any(line =>
            line.Contains("<auto-generated>") || line.Contains("GeneratedCode("));
}
