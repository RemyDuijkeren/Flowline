using System.ComponentModel;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using Flowline.Utils;
using Flowline.Validation;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace Flowline.Commands;

public class PushCommand(IAnsiConsole console, DataverseConnector dataverseConnector, PluginService pluginService, WebResourceService webResourceService, FlowlineRuntimeOptions runtimeOptions)
    : FlowlineCommand<PushCommand.Settings>(console, runtimeOptions)
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
        [Description("Solution to push")]
        public string? Solution { get; set; }

        [CommandOption("-s|--scope <SCOPE>")]
        [Description("Limit the push scope: all, webresources, plugins, or assemblyonly. Can be used more than once.")]
        public PushScope[] Scopes { get; set; } = [];

        [CommandOption("--dll <PATH>")]
        [Description("Prebuilt plugin assembly DLL to push without using a Flowline project")]
        public string? Dll { get; set; }

        [CommandOption("-w|--webresources <PATH>")]
        [Description("Web resource folder to push without using a Flowline project")]
        public string? WebResources { get; set; }

        [CommandOption("--dev <url>")]
        [Description("Use this dev environment URL")]
        public string? DevUrl { get; set; }

        [CommandOption("--save")]
        [Description("Keep Dataverse assets that are missing from source")]
        [DefaultValue(false)]
        public bool Save { get; set; } = false;

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

        var pushScope = standaloneMode
            ? ResolveStandaloneScope(settings)
            : ResolveProjectScope(settings);

        var pushAssemblyOnly = pushScope.HasFlag(PushScope.AssemblyOnly);
        var pushPlugins = !pushAssemblyOnly && pushScope.HasFlag(PushScope.Plugins);
        var pushWebResources = pushScope.HasFlag(PushScope.WebResources);

        var pluginsDll = await PreparePluginsForPushAsync(pushPlugins || pushAssemblyOnly, standaloneMode, settings, solutionName, standaloneParams, cancellationToken);
        var (webResourcesSyncFolder, actuallyPushWebResources) = await PrepareWebResourcesForPushAsync(pushWebResources, standaloneMode, settings, solutionName, standaloneParams, cancellationToken);

        var conn = await ConnectToDataverseAsync(dataverseConnector, environmentUrl, cancellationToken);

        if (pushAssemblyOnly && pluginsDll != null)
        {
            await pluginService.SyncAssemblyOnlyAsync(conn, pluginsDll, solutionName, runMode, cancellationToken).ConfigureAwait(false);
        }
        else if (pushPlugins && pluginsDll != null)
        {
            await pluginService.SyncSolutionAsync(conn, pluginsDll, solutionName, runMode, cancellationToken).ConfigureAwait(false);
        }

        if (actuallyPushWebResources)
        {
            await webResourceService.SyncSolutionAsync(conn, webResourcesSyncFolder!, solutionName, runMode: runMode, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        Console.Done("Assets pushed! Use 'sync' to keep it in flow.");

        return 0;
    }

    private static RunMode ResolveRunMode(Settings settings) =>
        settings.DryRun ? RunMode.DryRun
            : settings.Save ? RunMode.Save
            : RunMode.Normal;

    private StandaloneParams ResolveStandaloneParameters(Settings settings, bool standaloneMode)
    {
        if (!standaloneMode) return new StandaloneParams();

        var solutionName = ResolveStandaloneSolutionName(settings);
        var dllPath = !string.IsNullOrWhiteSpace(settings.Dll) ? ResolveStandaloneDllPath(settings) : null;
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
            throw new FlowlineException("Managed solutions are not supported for push.");

        return (devEnv, solutionName);
    }

    private async Task<string?> PreparePluginsForPushAsync(
        bool pushPlugins,
        bool standaloneMode,
        Settings settings,
        string solutionName,
        StandaloneParams standaloneParams,
        CancellationToken cancellationToken)
    {
        if (!pushPlugins) return null;

        string? pluginsDll = standaloneMode ? standaloneParams.DllPath : null;

        if (!standaloneMode)
        {
            var pluginsFolder = Path.Combine(RootFolder, AllSolutionsFolderName, solutionName, PluginsName);
            if (await DotNetUtils.BuildSolutionAsync(pluginsFolder, DotnetBuild.Release, settings.Verbose, cancellationToken) != 0)
                throw new FlowlineException("Plugins build failed — fix errors above.");

            pluginsDll = Path.Combine(pluginsFolder, "bin", "Release", "net462", "publish", $"{PluginsName}.dll");
        }

        if (pluginsDll == null || !File.Exists(pluginsDll))
            throw new FlowlineException(standaloneMode
                ? $"DLL not found: {settings.Dll}"
                : $"{PluginsName}.dll not found. Build the solution first.");

        Console.Info($"[bold]{Path.GetFileName(pluginsDll)}[/] found");
        Console.Verbose($"{pluginsDll}", RuntimeOptions.IsVerbose);

        return pluginsDll;
    }

    private async Task<(string?, bool)> PrepareWebResourcesForPushAsync(
        bool pushWebResources,
        bool standaloneMode,
        Settings settings,
        string solutionName,
        StandaloneParams standaloneParams,
        CancellationToken cancellationToken)
    {
        if (!pushWebResources) return (null, false);

        if (standaloneMode)
        {
            return (standaloneParams.WebResourcesPath, true);
        }

        var webResourcesFolder = Path.Combine(RootFolder, AllSolutionsFolderName, solutionName, WebResourcesName);
        var webResourcesSyncFolder = Path.Combine(webResourcesFolder, "dist");

        if (!Directory.Exists(webResourcesFolder))
        {
            Console.Skip("WebResources project not found — skipping");
            return (null, false);
        }

        if (await DotNetUtils.BuildSolutionAsync(webResourcesFolder, DotnetBuild.Release, settings.Verbose, cancellationToken) != 0)
            throw new FlowlineException("WebResources build failed — fix errors above.");

        return (webResourcesSyncFolder, true);
    }

    private class StandaloneParams
    {
        public string? SolutionName { get; set; }
        public string? DllPath { get; set; }
        public string? WebResourcesPath { get; set; }
    }

    internal static bool IsStandaloneMode(Settings settings) =>
        !string.IsNullOrWhiteSpace(settings.Dll) || !string.IsNullOrWhiteSpace(settings.WebResources);

    internal static PushScope ResolveProjectScope(Settings settings)
    {
        if (settings.Scopes.Length == 0) return PushScope.All;
        var scope = settings.Scopes.Aggregate(PushScope.None, (current, s) => current | s);
        if (scope.HasFlag(PushScope.AssemblyOnly) && scope.HasFlag(PushScope.Plugins))
            throw new FlowlineException("--scope assemblyonly and --scope plugins are mutually exclusive.");
        return scope;
    }

    internal static PushScope ResolveStandaloneScope(Settings settings)
    {
        if (settings.Scopes is [PushScope.AssemblyOnly])
            return PushScope.AssemblyOnly;

        var scope = PushScope.None;
        if (!string.IsNullOrWhiteSpace(settings.Dll)) scope |= PushScope.Plugins;
        if (!string.IsNullOrWhiteSpace(settings.WebResources)) scope |= PushScope.WebResources;
        return scope;
    }

    internal static void ValidateStandaloneMode(Settings settings, string rootFolder)
    {
        if (settings.Scopes.Length > 0)
        {
            var assemblyOnlyWithDll = settings.Scopes is [PushScope.AssemblyOnly] && !string.IsNullOrWhiteSpace(settings.Dll);
            if (!assemblyOnlyWithDll)
                throw new FlowlineException("--scope cannot be used together with --dll or --webresources. Use either project mode or standalone artifact mode.");
        }

        if (File.Exists(Path.Combine(rootFolder, ProjectConfig.s_configFileName)))
            throw new FlowlineException("--dll and --webresources cannot be used inside a Flowline project folder. Use project mode or run standalone push from another folder.");
    }

    internal static string ResolveStandaloneSolutionName(Settings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Solution))
            return settings.Solution.Trim();

        throw new FlowlineException("Solution name is required in standalone mode — pass it as the first argument.");
    }

    internal static string ResolveStandaloneEnvironmentUrl(Settings settings, DataverseConnector dataverseConnector)
    {
        if (!string.IsNullOrWhiteSpace(settings.DevUrl))
            return settings.DevUrl.Trim();

        var profile = dataverseConnector.GetCurrentResourceSpecificPacProfile();
        if (!string.IsNullOrWhiteSpace(profile?.Resource))
            return profile.Resource.Trim();

        throw new FlowlineException("Dev URL is required in standalone mode — use --dev <URL> or select a resource-specific PAC profile.");
    }

    internal static string ResolveStandaloneDllPath(Settings settings)
    {
        var path = Path.GetFullPath(settings.Dll!);
        if (!string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
            throw new FlowlineException("--dll must point to a .dll file.");

        if (!File.Exists(path))
            throw new FlowlineException($"DLL not found: {path}");

        return path;
    }

    internal static string ResolveStandaloneWebResourcesPath(Settings settings)
    {
        var path = Path.GetFullPath(settings.WebResources!);
        if (!Directory.Exists(path))
            throw new FlowlineException($"Web resources folder not found: {path}");

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
            throw new FlowlineException($"Solution '{solutionName}' not found in that environment.");

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
            throw new FlowlineException("Dev environment not found — check the URL or your PAC login.");

        if (env.Type == "Production")
            throw new FlowlineException("That's a Production environment — use a sandbox or dev instead.");

        console.Ok($"Dev: [bold]{env.DisplayName}[/] ({env.EnvironmentUrl})");
        return env;
    }
}
