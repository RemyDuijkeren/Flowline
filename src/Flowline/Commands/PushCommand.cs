using System.ComponentModel;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Services;
using Spectre.Console;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;
using Flowline.Utils;
using Flowline.Validation;

namespace Flowline.Commands;

public class PushCommand(IAnsiConsole console, DataverseConnector dataverseConnector, PluginService pluginService, WebResourceService webResourceService, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService, ILoggerFactory loggerFactory, SubprocessCapture capture)
    : FlowlineCommand<PushCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture)
{
    [Flags]
    public enum PushScope
    {
        None = 0,
        AssemblyOnly = 1,
        Plugins = 2,
        WebResources = 4,
        All = WebResources | Plugins
    }

    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "[solution]")]
        [Description("Solution to push (optional in project mode)")]
        public string? Solution { get; set; }

        [CommandOption("-s|--scope <SCOPE>")]
        [Description("Limit the push scope: all, webresources, plugins, or assemblyonly. Can be used more than once.")]
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
        [Description("Skip publishing web resources after sync")]
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

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var standaloneMode = IsStandaloneMode(settings);

        if (standaloneMode) ValidateStandaloneMode(settings, RootFolder);

        var runMode = ResolveRunMode(settings);
        var standaloneParams = ResolveStandaloneParameters(settings, standaloneMode);

        var environmentUrl = "";
        if (standaloneMode)
            environmentUrl = ResolveStandaloneEnvironmentUrl(settings, dataverseConnector);

        var (devEnv, solutionName) = await ResolveEnvironmentAndSolutionAsync(settings, standaloneMode, environmentUrl, standaloneParams, cancellationToken);

        if (!standaloneMode)
            environmentUrl = devEnv.EnvironmentUrl!;

        Logger.LogInformation("target={EnvironmentUrl} solution={SolutionName}", environmentUrl, solutionName);

        var pushScope = ResolveScope(settings, standaloneMode);
        var pushAssemblyOnly = pushScope.HasFlag(PushScope.AssemblyOnly);
        Logger.LogInformation("scope={Scope} mode={RunMode} standalone={Standalone}", pushScope, runMode, standaloneMode);

        var pluginsDll = (pushAssemblyOnly || pushScope.HasFlag(PushScope.Plugins))
            ? await PreparePluginsForPushAsync(standaloneMode, settings, solutionName, standaloneParams, cancellationToken)
            : null;
        var webResourcesSyncFolder = pushScope.HasFlag(PushScope.WebResources)
            ? await PrepareWebResourcesForPushAsync(standaloneMode, settings, solutionName, standaloneParams, cancellationToken)
            : null;

        var (conn, _) = await ConnectToDataverseAsync(dataverseConnector, environmentUrl, cancellationToken);

        if (pluginsDll != null)
        {
            if (pushAssemblyOnly)
            {
                Logger.LogInformation("Pushing assembly only: {Dll}", pluginsDll);
                await pluginService.SyncAssemblyOnlyAsync(conn, pluginsDll, solutionName, runMode, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Logger.LogInformation("Pushing plugins: {Dll}", pluginsDll);
                await pluginService.SyncSolutionAsync(conn, pluginsDll, solutionName, runMode, settings.Force, cancellationToken).ConfigureAwait(false);
            }
        }

        if (settings.NoPublish && !pushScope.HasFlag(PushScope.WebResources))
            Console.Warning("--no-publish has no effect: web resources not in scope.");

        if (webResourcesSyncFolder != null)
        {
            Logger.LogInformation("Pushing web resources: {Folder}", webResourcesSyncFolder);
            await webResourceService.SyncSolutionAsync(conn, webResourcesSyncFolder, solutionName, publishAfterSync: !settings.NoPublish, runMode: runMode, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (settings.NoPublish)
                Console.Skip("Publish — skipping (--no-publish active).");
        }

        Console.Done(runMode == RunMode.DryRun
            ? "Air push complete. Dataverse remains oblivious. Now do it for real without --dry-run!و"
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

    private async Task<(EnvironmentInfo, string)> ResolveEnvironmentAndSolutionAsync(
        Settings settings,
        bool standaloneMode,
        string environmentUrl,
        StandaloneParams standaloneParams,
        CancellationToken cancellationToken)
    {
        EnvironmentInfo devEnv;
        string solutionName;
        SolutionInfo slnInfo;

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
        }

        if (slnInfo.IsManaged)
            throw new FlowlineException(ExitCode.ValidationFailed, "Managed solutions are not supported for push.");

        return (devEnv, solutionName);
    }

    private async Task<string?> PreparePluginsForPushAsync(
        bool standaloneMode,
        Settings settings,
        string solutionName,
        StandaloneParams standaloneParams,
        CancellationToken cancellationToken)
    {
        string? pluginsDll = standaloneMode ? standaloneParams.DllPath : null;

        if (!standaloneMode)
        {
            var pluginsFolder = Path.Combine(RootFolder, AllSolutionsFolderName, solutionName, PluginsName);
            if (settings.NoBuild)
                Console.Skip("Build plugins — skipping (--no-build active)");
            else if (await DotNetUtils.BuildSolutionAsync(pluginsFolder, DotnetBuild.Release, _capture, cancellationToken) != 0)
                throw new FlowlineException(ExitCode.BuildFailed, "Plugins build failed — fix errors above.");

            pluginsDll = Path.Combine(pluginsFolder, "bin", "Release", "net462", "publish", $"{PluginsName}.dll");
        }

        if (pluginsDll == null || !File.Exists(pluginsDll))
            throw new FlowlineException(ExitCode.NotFound, standaloneMode
                ? $"Plugin file not found: {settings.PluginFile}"
                : $"{PluginsName}.dll not found — build the solution (Release) first, or drop --no-build.");

        Console.Verbose($"Found {pluginsDll}");
        Console.Info($"[bold]{ConsolePath.FormatRelativePath(pluginsDll)}[/] found");

        return pluginsDll;
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
            if (scope.HasFlag(PushScope.WebResources) && string.IsNullOrWhiteSpace(settings.WebResources))
                throw new FlowlineException(ExitCode.ValidationFailed, "--scope webresources requires --webresources.");
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

    internal static string ResolveStandalonePluginFilePath(Settings settings)
    {
        var path = Path.GetFullPath(settings.PluginFile!);
        var ext = Path.GetExtension(path);

        if (string.Equals(ext, ".nupkg", StringComparison.OrdinalIgnoreCase))
            throw new FlowlineException(ExitCode.ValidationFailed, "NuGet packages not yet supported — use a .dll file.");

        if (!string.Equals(ext, ".dll", StringComparison.OrdinalIgnoreCase))
            throw new FlowlineException(ExitCode.ValidationFailed, "--pluginFile must point to a .dll file.");

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
