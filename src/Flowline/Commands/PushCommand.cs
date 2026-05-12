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
        WebResources = 1,
        Plugins = 2,
        Pcf = 4,
        All = WebResources | Plugins | Pcf
    }

    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "[solution]")]
        [Description("Solution to push")]
        public string? Solution { get; set; }

        [CommandOption("-s|--scope <SCOPE>")]
        [Description("Limit the push scope: all, webresources, plugins, or pcf. Can be used more than once.")]
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
        public bool Save { get; set; } = false;

        [CommandOption("--dry-run")]
        [Description("Preview changes without touching Dataverse")]
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

        Console.Success("All good, let's go!");
    }

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var standaloneMode = IsStandaloneMode(settings);

        if (standaloneMode && !ValidateStandaloneMode(Console, settings, RootFolder)) return 1;

        var runMode = ResolveRunMode(settings);
        var standaloneParams = await ResolveStandaloneParametersAsync(settings, standaloneMode, cancellationToken);
        if (standaloneParams == null) return 1;

        var environmentUrl = "";
        if (standaloneMode)
        {
            environmentUrl = ResolveStandaloneEnvironmentUrl(Console, settings, dataverseConnector) ?? "";
            if (string.IsNullOrWhiteSpace(environmentUrl)) return 1;
        }

        var (devEnv, solutionName) = await ResolveEnvironmentAndSolutionAsync(settings, standaloneMode, environmentUrl, standaloneParams, cancellationToken);
        if (devEnv == null || solutionName == null) return 1;

        if (!standaloneMode)
            environmentUrl = devEnv.EnvironmentUrl!;

        var pushScope = standaloneMode
            ? ResolveStandaloneScope(settings)
            : ResolveProjectScope(settings);

        var pushPlugins = pushScope.HasFlag(PushScope.Plugins);
        var pushWebResources = pushScope.HasFlag(PushScope.WebResources);

        var pluginsDll = await PreparePluginsForPushAsync(pushPlugins, standaloneMode, settings, solutionName, standaloneParams, cancellationToken);
        if (pushPlugins && pluginsDll == null) return 1;

        var (webResourcesSyncFolder, actuallyPushWebResources) = await PrepareWebResourcesForPushAsync(pushWebResources, standaloneMode, settings, solutionName, standaloneParams, cancellationToken);
        if (pushWebResources && string.IsNullOrWhiteSpace(webResourcesSyncFolder)) return 1;

        var conn = await ConnectToDataverseAsync(environmentUrl, cancellationToken);
        if (conn == null) return 1;

        if (pushPlugins && pluginsDll != null)
        {
            await pluginService.SyncSolutionAsync(conn, pluginsDll, solutionName, runMode, cancellationToken).ConfigureAwait(false);
        }

        if (actuallyPushWebResources)
        {
            await webResourceService.SyncSolutionAsync(conn, webResourcesSyncFolder!, solutionName, runMode: runMode, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        Console.Success("[bold]:rocket: Assets pushed! Use 'sync' to keep it in flow.[/]");

        return 0;
    }

    private static RunMode ResolveRunMode(Settings settings) =>
        settings.DryRun ? RunMode.DryRun
            : settings.Save ? RunMode.Save
            : RunMode.Normal;

    private async Task<StandaloneParams?> ResolveStandaloneParametersAsync(Settings settings, bool standaloneMode, CancellationToken cancellationToken)
    {
        if (!standaloneMode) return new StandaloneParams();

        var solutionName = ResolveStandaloneSolutionName(Console, settings);
        if (solutionName == null) return null;

        var dllPath = !string.IsNullOrWhiteSpace(settings.Dll)
            ? ResolveStandaloneDllPath(Console, settings)
            : null;
        if (!string.IsNullOrWhiteSpace(settings.Dll) && dllPath == null) return null;

        var webResourcesPath = !string.IsNullOrWhiteSpace(settings.WebResources)
            ? ResolveStandaloneWebResourcesPath(Console, settings)
            : null;
        if (!string.IsNullOrWhiteSpace(settings.WebResources) && webResourcesPath == null) return null;

        return new StandaloneParams { SolutionName = solutionName, DllPath = dllPath, WebResourcesPath = webResourcesPath };
    }

    private async Task<(EnvironmentInfo?, string?)> ResolveEnvironmentAndSolutionAsync(
        Settings settings,
        bool standaloneMode,
        string environmentUrl,
        StandaloneParams standaloneParams,
        CancellationToken cancellationToken)
    {
        EnvironmentInfo? devEnv;
        string? solutionName;
        SolutionInfo? slnInfo;

        if (standaloneMode)
        {
            devEnv = await GetAndCheckStandaloneEnvironmentAsync(Console, environmentUrl, settings, cancellationToken).ConfigureAwait(false);
            if (devEnv == null) return (null, null);

            slnInfo = await GetAndCheckStandaloneSolutionAsync(Console, standaloneParams.SolutionName!, environmentUrl, settings, cancellationToken).ConfigureAwait(false);
            if (slnInfo == null) return (null, null);

            solutionName = standaloneParams.SolutionName;
        }
        else
        {
            devEnv = await GetAndCheckEnvironmentInfoAsync(EnvironmentRole.Dev, settings.DevUrl, settings, cancellationToken);
            if (devEnv == null) return (null, null);

            var (projectSln, slnInfoResult) = await GetAndCheckSolutionAsync(settings.Solution, devEnv.EnvironmentUrl!, false, settings, cancellationToken);
            if (projectSln == null || slnInfoResult == null) return (null, null);

            slnInfo = slnInfoResult;
            solutionName = projectSln.Name;
        }

        if (slnInfo.IsManaged)
        {
            Console.Error("Managed solutions are not supported for push.");
            return (null, null);
        }

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
            {
                return null;
            }

            pluginsDll = Path.Combine(pluginsFolder, "bin", "Release", "net462", "publish", $"{PluginsName}.dll");
        }

        if (pluginsDll == null || !File.Exists(pluginsDll))
        {
            Console.Error(standaloneMode
                ? $"DLL not found: {settings.Dll}"
                : $"{PluginsName}.dll not found. Build the solution first.");
            return null;
        }

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
        {
            return (null, false);
        }

        return (webResourcesSyncFolder, true);
    }

    private async Task<IOrganizationServiceAsync2?> ConnectToDataverseAsync(string environmentUrl, CancellationToken cancellationToken)
    {
        IOrganizationServiceAsync2? conn = null;

        await Console.Status().FlowlineSpinner().StartAsync("Connecting to Dataverse...", async ctx =>
        {
            var profile = dataverseConnector.FindBestProfile(environmentUrl);

            if (profile == null)
            {
                Console.Error("No PAC profile found — run 'pac auth create' first.");
                return;
            }

            try
            {
                conn = await dataverseConnector.ConnectViaPacAsync(profile, environmentUrl, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                Console.Error(ex);
            }
        });

        if (conn != null)
            Console.Success("Connected");

        return conn;
    }

    private class StandaloneParams
    {
        public string? SolutionName { get; set; }
        public string? DllPath { get; set; }
        public string? WebResourcesPath { get; set; }
    }

    internal static bool IsStandaloneMode(Settings settings) =>
        !string.IsNullOrWhiteSpace(settings.Dll) || !string.IsNullOrWhiteSpace(settings.WebResources);

    internal static PushScope ResolveProjectScope(Settings settings) =>
        settings.Scopes.Length == 0
            ? PushScope.All
            : settings.Scopes.Aggregate(PushScope.None, (current, scope) => current | scope);

    internal static PushScope ResolveStandaloneScope(Settings settings)
    {
        var scope = PushScope.None;
        if (!string.IsNullOrWhiteSpace(settings.Dll)) scope |= PushScope.Plugins;
        if (!string.IsNullOrWhiteSpace(settings.WebResources)) scope |= PushScope.WebResources;
        return scope;
    }

    internal static bool ValidateStandaloneMode(IAnsiConsole console, Settings settings, string rootFolder)
    {
        if (settings.Scopes.Length > 0)
        {
            console.Error("--scope cannot be used together with --dll or --webresources. Use either project mode or standalone artifact mode");
            return false;
        }

        if (File.Exists(Path.Combine(rootFolder, ProjectConfig.s_configFileName)))
        {
            console.Error("--dll and --webresources cannot be used inside a Flowline project folder. Use project mode or run standalone push from another folder");
            return false;
        }

        return true;
    }

    internal static string? ResolveStandaloneSolutionName(IAnsiConsole console, Settings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Solution))
            return settings.Solution.Trim();

        console.Error("Solution name is required in standalone mode — pass it as the first argument");
        return null;
    }

    internal static string? ResolveStandaloneEnvironmentUrl(IAnsiConsole console, Settings settings, DataverseConnector dataverseConnector)
    {
        if (!string.IsNullOrWhiteSpace(settings.DevUrl))
            return settings.DevUrl.Trim();

        var profile = dataverseConnector.GetCurrentResourceSpecificPacProfile();
        if (!string.IsNullOrWhiteSpace(profile?.Resource))
            return profile.Resource.Trim();

        console.Error("Dev URL is required in standalone mode — use --dev <URL> or select a resource-specific PAC profile");
        return null;
    }

    internal static string? ResolveStandaloneDllPath(IAnsiConsole console, Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Dll))
            return null;

        var path = Path.GetFullPath(settings.Dll);
        if (!string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            console.Error("--dll must point to a .dll file");
            return null;
        }

        if (!File.Exists(path))
        {
            console.Error($"DLL not found: {path}");
            return null;
        }

        return path;
    }

    internal static string? ResolveStandaloneWebResourcesPath(IAnsiConsole console, Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.WebResources))
            return null;

        var path = Path.GetFullPath(settings.WebResources);
        if (!Directory.Exists(path))
        {
            console.Error($"Web resources folder not found: {path}");
            return null;
        }

        return path;
    }

    static async Task<SolutionInfo?> GetAndCheckStandaloneSolutionAsync(
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
        {
            console.Error($"[bold]{solutionName}[/] not found in that environment");
            return null;
        }

        console.Success($"Solution: [bold]{solutionName}[/] (managed: {remoteSln.IsManaged})");
        return remoteSln;
    }

    static async Task<EnvironmentInfo?> GetAndCheckStandaloneEnvironmentAsync(
        IAnsiConsole console,
        string environmentUrl,
        Settings settings,
        CancellationToken cancellationToken)
    {
        EnvironmentInfo? env = await console.Status().FlowlineSpinner().StartAsync(
            $"Checking dev [bold]{environmentUrl}[/]...",
            ctx => FlowlineValidator.Default.GetEnvironmentInfoByUrlAsync(environmentUrl, settings, cancellationToken));

        if (env == null)
        {
            console.Error("Dev environment not found — check the URL or your PAC login");
            return null;
        }

        if (env.Type == "Production")
        {
            console.Error("That's a Production environment — use a sandbox or dev instead");
            return null;
        }

        console.Success($"Dev: [bold]{env.DisplayName}[/] ({env.EnvironmentUrl})");
        return env;
    }
}
