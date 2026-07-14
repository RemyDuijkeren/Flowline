using System.ComponentModel;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using Spectre.Console;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;
using Flowline.Utils;
using Flowline.Validation;

namespace Flowline.Commands;

public class PushCommand(IAnsiConsole console, DataverseConnector dataverseConnector, PluginService pluginService, WebResourceService webResourceService, FormEventService formEventService, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService, ILoggerFactory loggerFactory, SubprocessCapture capture)
    : FlowlineCommand<PushCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture)
{
    [Flags]
    public enum PushScope
    {
        None = 0,
        AssemblyOnly = 1,
        Plugins = 2,
        WebResources = 4,
        // Additive, not a replacement for WebResources' existing bundling: WebResources alone still runs
        // both web resource sync and form event registration, unchanged. FormEvents lets that registration
        // step run on its own — reconciling // flowline:onload/onsave annotations against an already-built
        // dist/ folder without also syncing web resource content — e.g. after editing only an annotation.
        FormEvents = 8,
        All = WebResources | Plugins
    }

    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "[solution]")]
        [Description("Solution to push (optional in project mode)")]
        public string? Solution { get; set; }

        [CommandOption("-s|--scope <SCOPE>")]
        [Description("Limit the push scope: all, webresources, formevents, plugins, or assemblyonly. Can be used more than once.")]
        public PushScope[] Scopes { get; set; } = [];

        [CommandOption("-p|--pluginFile <PATH>")]
        [Description("Prebuilt plugin file (.dll) to push without using a Flowline project")]
        public string? PluginFile { get; set; }

        [CommandOption("-w|--webresources <PATH>")]
        [Description("Web resource folder to push without using a Flowline project")]
        public string? WebResources { get; set; }

        [CommandOption("--dev <url>")]
        [Description("Use this dev environment URL")]
        public string? DevUrl { get; set; }

        [CommandOption("--no-delete")]
        [Description("Push without deleting any Dataverse assets that are missing from source")]
        [DefaultValue(false)]
        public bool NoDelete { get; set; } = false;

        [CommandOption("--no-build")]
        [Description("Skip 'dotnet build' and push the existing Release artifacts")]
        [DefaultValue(false)]
        public bool NoBuild { get; set; } = false;

        [CommandOption("--no-publish")]
        [Description("Skip publishing web resources and form event handlers after sync")]
        [DefaultValue(false)]
        public bool NoPublish { get; set; } = false;

        [CommandOption("--dry-run")]
        [Description("Preview changes without touching Dataverse")]
        [DefaultValue(false)]
        public bool DryRun { get; set; } = false;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!IsStandaloneMode(settings))
            return await base.ExecuteAsync(context, settings, cancellationToken).ConfigureAwait(false);

        InitializeRuntimeOptions(settings);
        await CheckSetupAsync(settings, cancellationToken).ConfigureAwait(false);
        return await ExecuteFlowlineAsync(context, settings, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task CheckSetupAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (!IsStandaloneMode(settings))
        {
            await base.CheckSetupAsync(settings, cancellationToken).ConfigureAwait(false);
            return;
        }

        await Console.Status().FlowlineSpinner().StartAsync("Checking your setup...", async ctx =>
        {
            await FlowlineValidator.Default.EnsurePacCliAsync(settings, cancellationToken);
        });

        Console.Ok("All good, let's go!");
    }

    internal static readonly string[] ValidSpecifiers =
        ["delete-orphans", "recreate-assembly", "delete-form-handlers", "config", "all"];
    protected override string[] ValidForceSpecifiers => ValidSpecifiers;

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var standaloneMode = IsStandaloneMode(settings);

        if (standaloneMode) ValidateStandaloneMode(settings, RootFolder);

        var runMode = ResolveRunMode(settings);
        var standaloneParams = ResolveStandaloneParameters(settings, standaloneMode);

        var environmentUrl = "";
        if (standaloneMode)
            environmentUrl = ResolveStandaloneEnvironmentUrl(settings, dataverseConnector);

        var (devEnv, solutionName, forceClassicPluginAssembly) = await ResolveEnvironmentAndSolutionAsync(settings, standaloneMode, environmentUrl, standaloneParams, cancellationToken);

        if (!standaloneMode)
            environmentUrl = devEnv.EnvironmentUrl!;

        Logger.LogInformation("target={EnvironmentUrl} solution={SolutionName}", environmentUrl, solutionName);

        var pushScope = ResolveScope(settings, standaloneMode);
        var pushAssemblyOnly = pushScope.HasFlag(PushScope.AssemblyOnly);
        Logger.LogInformation("scope={Scope} mode={RunMode} standalone={Standalone}", pushScope, runMode, standaloneMode);

        var (pluginsPushPath, pluginAssemblyName) = (pushAssemblyOnly || pushScope.HasFlag(PushScope.Plugins))
            ? await PreparePluginsForPushAsync(standaloneMode, settings, solutionName, forceClassicPluginAssembly, standaloneParams, cancellationToken)
            : (null, null);
        // FormEvents reads its annotations from the same built dist/ folder web resource sync uses, so
        // either scope alone needs it prepared — WebResources still implies FormEvents (unchanged default
        // bundling); FormEvents lets the registration step run on its own, against an already-pushed dist/.
        var runFormEvents = pushScope.HasFlag(PushScope.WebResources) || pushScope.HasFlag(PushScope.FormEvents);
        var webResourcesSyncFolder = runFormEvents
            ? await PrepareWebResourcesForPushAsync(standaloneMode, settings, solutionName, standaloneParams, cancellationToken)
            : null;

        var (conn, _) = await ConnectToDataverseAsync(dataverseConnector, environmentUrl, cancellationToken);

        var pushedChanges = false;

        if (pluginsPushPath != null)
        {
            // R1/KD1/KD6: a .nupkg alongside the classic .dll (or an explicit --pluginFile .nupkg) routes
            // to the shared package entry point regardless of --scope assemblyonly — there is no separate
            // "assembly only" package variant, since the package path always reconciles steps (KD4).
            if (IsPackagePush(pluginsPushPath))
            {
                Logger.LogInformation("Pushing plugin package: {Nupkg}", pluginsPushPath);
                pushedChanges |= await pluginService.SyncSolutionFromPackageAsync(conn, pluginsPushPath, pluginAssemblyName!, solutionName, runMode, cancellationToken).ConfigureAwait(false);
            }
            else if (pushAssemblyOnly)
            {
                Logger.LogInformation("Pushing assembly only: {Dll}", pluginsPushPath);
                pushedChanges |= await pluginService.SyncAssemblyOnlyAsync(conn, pluginsPushPath, solutionName, runMode, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Logger.LogInformation("Pushing plugins: {Dll}", pluginsPushPath);
                pushedChanges |= await pluginService.SyncSolutionAsync(conn, pluginsPushPath, solutionName, runMode,
                    settings.HasForce("delete-orphans"), settings.HasForce("recreate-assembly"), cancellationToken).ConfigureAwait(false);
            }
        }

        if (settings.NoPublish && !runFormEvents)
            Console.Warning("--no-publish has no effect: web resources/form events not in scope.");

        if (webResourcesSyncFolder != null)
        {
            var dryRun = runMode == RunMode.DryRun;
            var publishAfterSync = !settings.NoPublish;
            // U2: resolved once per push — one form-event identity cache file per environment, so a later
            // rename-detection unit (U3) has data from every successful resolution to suggest from.
            var formEventCachePath = FlowlineStoragePaths.GetFormEventCachePath(environmentUrl);

            // KTD12: cleanup runs before web resources are created/updated/deleted — removes stale/orphaned
            // form event handlers (R14) so a pending web-resource delete never trips Dataverse's
            // "referenced by N other components" dependency fault.
            pushedChanges |= await formEventService.CleanupOrphanedAsync(conn, webResourcesSyncFolder, solutionName, settings.HasForce("delete-form-handlers"), dryRun, publishAfterSync, formEventCachePath, cancellationToken).ConfigureAwait(false);

            if (pushScope.HasFlag(PushScope.WebResources))
            {
                Logger.LogInformation("Pushing web resources: {Folder}", webResourcesSyncFolder);
                pushedChanges |= await webResourceService.SyncSolutionAsync(conn, webResourcesSyncFolder, solutionName, publishAfterSync: publishAfterSync, runMode: runMode, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            if (settings.NoPublish)
                Console.Skip("Publish — skipping (--no-publish active).");

            // R10a: registration runs strictly after web resources are pushed, same scope gate — new/updated
            // handlers can only reference libraries that already exist in Dataverse.
            pushedChanges |= await formEventService.RegisterAsync(conn, webResourcesSyncFolder, solutionName, settings.HasForce("delete-form-handlers"), dryRun, publishAfterSync, formEventCachePath, cancellationToken).ConfigureAwait(false);
        }

        Console.Done(runMode == RunMode.DryRun
            ? "Air push complete. Dataverse remains oblivious. Now do it for real without --dry-run!"
            : !pushedChanges
                ? "Nothing to push — already up to date."
                : standaloneMode
                    ? "Assets pushed! (•ᴗ•)و"
                    : "Assets pushed! Use 'sync' to keep it in flow. (•ᴗ•)و");

        return 0;
    }

    private static RunMode ResolveRunMode(Settings settings) =>
        settings.DryRun ? RunMode.DryRun
            : settings.NoDelete ? RunMode.NoDelete
            : RunMode.Normal;

    private StandaloneParams ResolveStandaloneParameters(Settings settings, bool standaloneMode)
    {
        if (!standaloneMode) return new StandaloneParams();

        var solutionName = ResolveStandaloneSolutionName(settings);
        var dllPath = !string.IsNullOrWhiteSpace(settings.PluginFile) ? ResolveStandalonePluginFilePath(settings) : null;
        var webResourcesPath = !string.IsNullOrWhiteSpace(settings.WebResources) ? ResolveStandaloneWebResourcesPath(settings) : null;

        return new StandaloneParams { SolutionName = solutionName, DllPath = dllPath, WebResourcesPath = webResourcesPath };
    }

    private async Task<(EnvironmentInfo, string, bool)> ResolveEnvironmentAndSolutionAsync(
        Settings settings,
        bool standaloneMode,
        string environmentUrl,
        StandaloneParams standaloneParams,
        CancellationToken cancellationToken)
    {
        EnvironmentInfo devEnv;
        string solutionName;
        SolutionInfo slnInfo;
        var forceClassicPluginAssembly = false;

        if (standaloneMode)
        {
            devEnv = await GetAndCheckStandaloneEnvironmentAsync(Console, environmentUrl, settings, cancellationToken).ConfigureAwait(false);
            slnInfo = await GetAndCheckStandaloneSolutionAsync(Console, standaloneParams.SolutionName!, environmentUrl, settings, cancellationToken).ConfigureAwait(false);
            solutionName = standaloneParams.SolutionName!;
        }
        else
        {
            devEnv = await GetAndCheckEnvironmentInfoAsync(EnvironmentRole.Dev, settings.DevUrl, settings, cancellationToken);
            var (projectSln, slnInfoResult) = await GetAndCheckSolutionAsync(settings.Solution, devEnv.EnvironmentUrl!, cancellationToken: cancellationToken, settings: settings);
            slnInfo = slnInfoResult;
            solutionName = projectSln.Name;
            forceClassicPluginAssembly = projectSln.ForceClassicPluginAssembly;
        }

        if (slnInfo.IsManaged)
            throw new FlowlineException(ExitCode.ValidationFailed, "Managed solutions are not supported for push.");

        return (devEnv, solutionName, forceClassicPluginAssembly);
    }

    private async Task<(string? PushPath, string? AssemblyName)> PreparePluginsForPushAsync(
        bool standaloneMode,
        Settings settings,
        string solutionName,
        bool forceClassicPluginAssembly,
        StandaloneParams standaloneParams,
        CancellationToken cancellationToken)
    {
        string? pluginsDll = standaloneMode ? standaloneParams.DllPath : null;
        string? releaseOutputRoot = null;

        if (!standaloneMode)
        {
            var pluginsFolder = Path.Combine(RootFolder, AllSolutionsFolderName, solutionName, PluginsName);
            if (settings.NoBuild)
                Console.Skip("Build plugins — skipping (--no-build active)");
            else if (await DotNetUtils.BuildSolutionAsync(pluginsFolder, DotnetBuild.Release, _capture, cancellationToken) != 0)
                throw new FlowlineException(ExitCode.BuildFailed, "Plugins build failed — fix errors above.");

            // dotnet pack drops the .nupkg at bin/Release/ directly — a sibling of, not inside, the
            // net462/publish/ folder the classic .dll lives in (confirmed against a real `pac plugin init`
            // build). ResolvePluginPushPath searches this whole root, not just the .dll's own folder.
            releaseOutputRoot = Path.Combine(pluginsFolder, "bin", "Release");
            pluginsDll = Path.Combine(releaseOutputRoot, "net462", "publish", $"{PluginsName}.dll");
        }

        if (pluginsDll == null || !File.Exists(pluginsDll))
            throw new FlowlineException(ExitCode.NotFound, standaloneMode
                ? $"Plugin file not found: {settings.PluginFile}"
                : $"{PluginsName}.dll not found — build the solution (Release) first, or drop --no-build.");

        // R1/KD1: a .nupkg anywhere under the build output root routes to the package deployment path
        // automatically, unless ForceClassicPluginAssembly (U2/R2) opts back into the classic path.
        // Standalone mode already has its final path resolved (ResolveStandalonePluginFilePath).
        var pluginsPushPath = standaloneMode ? pluginsDll : ResolvePluginPushPath(pluginsDll, releaseOutputRoot!, forceClassicPluginAssembly);

        // Project mode's build output assembly name is deterministic (PluginsName). Standalone mode has
        // no such project context — for a .nupkg, the file itself typically embeds its NuGet version
        // (e.g. "MyPlugins.1.0.0.nupkg"), so the filename minus extension does NOT match the actual
        // reflected assembly name inside the package (R2a) and SyncSolutionFromPackageAsync's primary-
        // assembly match would fail. Reflect the package here to resolve the real name instead of guessing.
        var assemblyName = standaloneMode && IsPackagePush(pluginsPushPath)
            ? ResolveStandalonePackageAssemblyName(pluginsPushPath, Console)
            : standaloneMode ? Path.GetFileNameWithoutExtension(pluginsDll) : PluginsName;

        Console.Verbose($"Found {pluginsPushPath}");
        Console.Info($"[bold]{ConsolePath.FormatRelativePath(pluginsPushPath)}[/] found");

        return (pluginsPushPath, assemblyName);
    }

    private async Task<string?> PrepareWebResourcesForPushAsync(
        bool standaloneMode,
        Settings settings,
        string solutionName,
        StandaloneParams standaloneParams,
        CancellationToken cancellationToken)
    {
        if (standaloneMode) return standaloneParams.WebResourcesPath;

        var webResourcesFolder = Path.Combine(RootFolder, AllSolutionsFolderName, solutionName, WebResourcesName);
        var webResourcesSyncFolder = Path.Combine(webResourcesFolder, "dist");

        if (!Directory.Exists(webResourcesFolder))
        {
            Console.Skip("Web resources project not found — skipping");
            return null;
        }

        if (settings.NoBuild)
            Console.Skip("Build web resources - skipping (--no-build active)");
        else if (await DotNetUtils.BuildSolutionAsync(webResourcesFolder, DotnetBuild.Release, _capture, cancellationToken) != 0)
            throw new FlowlineException(ExitCode.BuildFailed, "Web resources build failed — fix errors above.");

        EnsureBuiltWebResources(webResourcesSyncFolder);

        Console.Verbose($"Found {webResourcesSyncFolder}");
        Console.Info($"[bold]{ConsolePath.FormatRelativePath(webResourcesSyncFolder)}[/] found");

        return webResourcesSyncFolder;
    }

    // Safety guard: an empty 'dist' makes push compute deletes for every remote web
    // resource. Refuse before that can happen — independent of --no-build.
    internal static void EnsureBuiltWebResources(string distFolder)
    {
        if (!Directory.Exists(distFolder) || !Directory.EnumerateFiles(distFolder, "*", SearchOption.AllDirectories).Any())
            throw new FlowlineException(ExitCode.NotFound,
                "No web resources in 'dist' — refusing to push, since this would remove every web resource in the solution. Build first, or check your source.");
    }

    private class StandaloneParams
    {
        public string? SolutionName { get; set; }
        public string? DllPath { get; set; }
        public string? WebResourcesPath { get; set; }
    }

    internal static bool IsStandaloneMode(Settings settings) =>
        !string.IsNullOrWhiteSpace(settings.PluginFile) || !string.IsNullOrWhiteSpace(settings.WebResources);

    internal static PushScope ResolveScope(Settings settings, bool standaloneMode)
    {
        PushScope scope;
        if (settings.Scopes.Length > 0)
        {
            scope = settings.Scopes.Aggregate(PushScope.None, (current, s) => current | s);
            if (scope.HasFlag(PushScope.AssemblyOnly) && scope.HasFlag(PushScope.Plugins))
                throw new FlowlineException(ExitCode.ValidationFailed, "--scope assemblyonly and --scope plugins are mutually exclusive.");
        }
        else if (standaloneMode)
        {
            scope = PushScope.None;
            if (!string.IsNullOrWhiteSpace(settings.PluginFile)) scope |= PushScope.Plugins;
            if (!string.IsNullOrWhiteSpace(settings.WebResources)) scope |= PushScope.WebResources;
        }
        else
        {
            scope = PushScope.All;
        }

        if (standaloneMode)
        {
            if ((scope.HasFlag(PushScope.Plugins) || scope.HasFlag(PushScope.AssemblyOnly)) && string.IsNullOrWhiteSpace(settings.PluginFile))
                throw new FlowlineException(ExitCode.ValidationFailed, "--scope plugins/assemblyonly requires --pluginFile.");
            if ((scope.HasFlag(PushScope.WebResources) || scope.HasFlag(PushScope.FormEvents)) && string.IsNullOrWhiteSpace(settings.WebResources))
                throw new FlowlineException(ExitCode.ValidationFailed, "--scope webresources/formevents requires --webresources.");
        }

        return scope;
    }

    internal static void ValidateStandaloneMode(Settings settings, string rootFolder)
    {
        if (File.Exists(Path.Combine(rootFolder, ProjectConfig.s_configFileName)))
            throw new FlowlineException(ExitCode.ValidationFailed, "--pluginFile and --webresources cannot be used inside a Flowline project folder. Use project mode or run standalone push from another folder.");
    }

    internal static string ResolveStandaloneSolutionName(Settings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Solution))
            return settings.Solution.Trim();

        throw new FlowlineException(ExitCode.ValidationFailed, "Solution name is required in standalone mode — pass it as the first argument.");
    }

    internal static string ResolveStandaloneEnvironmentUrl(Settings settings, DataverseConnector dataverseConnector)
    {
        if (!string.IsNullOrWhiteSpace(settings.DevUrl))
            return settings.DevUrl.Trim();

        var profile = dataverseConnector.GetCurrentResourceSpecificPacProfile();
        if (!string.IsNullOrWhiteSpace(profile?.Resource))
            return profile.Resource.Trim();

        throw new FlowlineException(ExitCode.ValidationFailed, "Dev URL is required in standalone mode — use --dev <URL> or select a resource-specific PAC profile.");
    }

    // R1/KD1: build output has a .dll for certain (regression-checked above); if a .nupkg exists anywhere
    // under the build output root — dotnet pack drops it at bin/Release/ directly, a sibling of the
    // net462/publish/ folder the .dll itself lives in, not alongside the .dll — and the solution hasn't
    // opted into ForceClassicPluginAssembly (U2/R2), use it.
    internal static string ResolvePluginPushPath(string dllPath, string buildOutputRoot, bool forceClassicPluginAssembly)
    {
        if (forceClassicPluginAssembly || !Directory.Exists(buildOutputRoot))
            return dllPath;

        var nupkgPath = Directory.GetFiles(buildOutputRoot, "*.nupkg", SearchOption.AllDirectories).FirstOrDefault();
        return nupkgPath ?? dllPath;
    }

    // KD6: the single decision point shared by both project-mode auto-detection and standalone
    // --pluginFile — whichever path produced pluginsPushPath, its extension alone decides the route.
    internal static bool IsPackagePush(string pluginPushPath) =>
        string.Equals(Path.GetExtension(pluginPushPath), ".nupkg", StringComparison.OrdinalIgnoreCase);

    // R2a: standalone mode has no project context to anchor a deterministic assembly name to (unlike
    // project mode's PluginsName constant) — the .nupkg's own filename typically embeds its NuGet
    // version and does not match the reflected assembly name inside it. Reflect the package once here
    // to resolve the real name rather than guessing from the filename.
    internal static string ResolveStandalonePackageAssemblyName(string nupkgPath, IAnsiConsole console)
    {
        var assemblies = new PluginAssemblyReader(console).AnalyzePackage(nupkgPath);

        if (assemblies.Count == 1)
            return assemblies[0].Name;

        if (assemblies.Count == 0)
            // R3a fires inside SyncSolutionFromPackageAsync regardless of what name is passed here —
            // any placeholder is fine, it never gets used to resolve a "primary" assembly.
            return Path.GetFileNameWithoutExtension(nupkgPath);

        throw new FlowlineException(ExitCode.ValidationFailed,
            $"--pluginFile package contains {assemblies.Count} plugin-bearing assemblies " +
            $"({string.Join(", ", assemblies.Select(a => a.Name))}) — standalone mode can't determine which " +
            "one is primary without project context. Push from the project instead.");
    }

    internal static string ResolveStandalonePluginFilePath(Settings settings)
    {
        var path = Path.GetFullPath(settings.PluginFile!);
        var ext = Path.GetExtension(path);

        // R2a/KD6: .nupkg is accepted here and routes to the exact same package entry point project mode
        // uses — no separate standalone implementation.
        if (!string.Equals(ext, ".dll", StringComparison.OrdinalIgnoreCase) && !string.Equals(ext, ".nupkg", StringComparison.OrdinalIgnoreCase))
            throw new FlowlineException(ExitCode.ValidationFailed, "--pluginFile must point to a .dll or .nupkg file.");

        if (!File.Exists(path))
            throw new FlowlineException(ExitCode.NotFound, $"Plugin file not found: {path}");

        return path;
    }

    internal static string ResolveStandaloneWebResourcesPath(Settings settings)
    {
        var path = Path.GetFullPath(settings.WebResources!);
        if (!Directory.Exists(path))
            throw new FlowlineException(ExitCode.NotFound, $"Web resources folder not found: {path}");

        return path;
    }

    static async Task<SolutionInfo> GetAndCheckStandaloneSolutionAsync(
        IAnsiConsole console,
        string solutionName,
        string environmentUrl,
        Settings settings,
        CancellationToken cancellationToken)
    {
        SolutionInfo? remoteSln = await console.Status().FlowlineSpinner().StartAsync(
            $"Looking up [bold]{solutionName}[/]...",
            ctx => FlowlineValidator.Default.GetSolutionInfoAsync(environmentUrl, solutionName, includeManaged: false, settings, cancellationToken));
        if (remoteSln == null)
            throw new FlowlineException(ExitCode.NotFound, $"Solution '{solutionName}' not found in that environment.");

        console.Ok($"Solution: [bold]{solutionName}[/] (managed: {remoteSln.IsManaged})");
        return remoteSln;
    }

    static async Task<EnvironmentInfo> GetAndCheckStandaloneEnvironmentAsync(
        IAnsiConsole console,
        string environmentUrl,
        Settings settings,
        CancellationToken cancellationToken)
    {
        EnvironmentInfo? env = await console.Status().FlowlineSpinner().StartAsync(
            $"Checking dev [bold]{environmentUrl}[/]...",
            ctx => FlowlineValidator.Default.GetEnvironmentInfoByUrlAsync(environmentUrl, settings, cancellationToken));

        if (env == null)
            throw new FlowlineException(ExitCode.ConnectionFailed, "Dev environment not found — check the URL or your PAC login.");

        if (env.Type == "Production")
            throw new FlowlineException(ExitCode.ValidationFailed, "That's a Production environment — use a sandbox or dev instead.");

        console.Ok($"Dev: [bold]{env.DisplayName}[/] ({env.EnvironmentUrl})");
        return env;
    }
}
