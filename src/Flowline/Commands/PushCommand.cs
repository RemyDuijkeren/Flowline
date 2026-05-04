using System.ComponentModel;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using Flowline.Utils;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace Flowline.Commands;

public class PushCommand(DataverseConnector dataverseConnector, PluginService pluginService, WebResourceService webResourceService, AnsiConsoleOutput output)
    : FlowlineCommand<PushCommand.Settings>
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

        await AnsiConsole.Status().FlowlineSpinner().StartAsync("Checking your setup...", async ctx =>
        {
            await PacUtils.AssertPacCliInstalledAsync(settings.Verbose, cancellationToken);
        });

        AnsiConsole.MarkupLine("[green]All good, let's go![/]");
    }

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        output.IsVerbose = settings.Verbose;
        var standaloneMode = IsStandaloneMode(settings);

        if (standaloneMode && !ValidateStandaloneMode(settings, RootFolder)) return 1;

        var runMode = settings.DryRun ? RunMode.DryRun
                    : settings.Save   ? RunMode.Save
                    : RunMode.Normal;
        if (runMode == RunMode.Save)   AnsiConsole.MarkupLine("[dim]Save mode: missing source assets stay in Dataverse[/]");
        if (runMode == RunMode.DryRun) AnsiConsole.MarkupLine("[dim]Dry run: preview only[/]");
        if (settings.Force) AnsiConsole.MarkupLine("[dim]Force mode: safety checks off[/]");

        var solutionName = standaloneMode
            ? ResolveStandaloneSolutionName(settings)
            : null;
        if (standaloneMode && solutionName == null) return 1;

        var standaloneDllPath = (string?)null;
        var standaloneWebResourcesPath = (string?)null;
        if (standaloneMode)
        {
            if (!string.IsNullOrWhiteSpace(settings.Dll))
            {
                standaloneDllPath = ResolveStandaloneDllPath(settings);
                if (standaloneDllPath == null) return 1;
            }

            if (!string.IsNullOrWhiteSpace(settings.WebResources))
            {
                standaloneWebResourcesPath = ResolveStandaloneWebResourcesPath(settings);
                if (standaloneWebResourcesPath == null) return 1;
            }
        }

        EnvironmentInfo? devEnv = null;
        string environmentUrl;
        if (standaloneMode)
        {
            environmentUrl = ResolveStandaloneEnvironmentUrl(settings, dataverseConnector) ?? "";
            if (string.IsNullOrWhiteSpace(environmentUrl)) return 1;

            devEnv = await GetAndCheckStandaloneEnvironmentAsync(environmentUrl, settings, cancellationToken).ConfigureAwait(false);
            if (devEnv == null) return 1;
        }
        else
        {
            devEnv = await GetAndCheckEnvironmentInfoAsync(EnvironmentRole.Dev, settings.DevUrl, settings, cancellationToken);
            if (devEnv == null) return 1;
            environmentUrl = devEnv.EnvironmentUrl!;
        }

        ProjectSolution? projectSln = null;
        SolutionInfo? slnInfo;
        if (standaloneMode)
        {
            slnInfo = await GetAndCheckStandaloneSolutionAsync(solutionName!, environmentUrl, settings, cancellationToken).ConfigureAwait(false);
            if (slnInfo == null) return 1;
        }
        else
        {
            (projectSln, slnInfo) = await GetAndCheckSolutionAsync(settings.Solution, environmentUrl, false, settings, cancellationToken);
            if (projectSln == null || slnInfo == null) return 1;
            solutionName = projectSln.Name;
        }

        if (slnInfo.IsManaged)
        {
            AnsiConsole.MarkupLine("[red]Managed solutions are not supported for push.[/]");
            return 1;
        }

        var pushScope = standaloneMode
            ? ResolveStandaloneScope(settings)
            : ResolveProjectScope(settings);
        AnsiConsole.MarkupLine($"[dim]Scope: {pushScope}[/]");

        var pushPlugins = pushScope.HasFlag(PushScope.Plugins);
        var pushWebResources = pushScope.HasFlag(PushScope.WebResources);

        string? extensionsDll = null;
        if (pushPlugins)
        {
            extensionsDll = standaloneMode ? standaloneDllPath : null;

            if (!standaloneMode)
            {
                var extensionsFolder = Path.Combine(RootFolder, AllSolutionsFolderName, solutionName!, ExtensionsName);
                if (await DotNetUtils.BuildSolutionAsync(extensionsFolder, DotnetBuild.Release, settings.Verbose, cancellationToken) != 0)
                {
                    return 1;
                }

                extensionsDll = Path.Combine(extensionsFolder, "bin", "Release", "net462", "publish", $"{ExtensionsName}.dll");
            }

            if (extensionsDll == null || !File.Exists(extensionsDll))
            {
                AnsiConsole.MarkupLine(standaloneMode
                    ? $"[red]DLL not found: {settings.Dll}[/]"
                    : $"[red]{ExtensionsName}.dll not found. Build the solution first.[/]");
                return 1;
            }
            AnsiConsole.MarkupLine($"[green][bold]{Path.GetFileName(extensionsDll)}[/] found[/]");
            output.Verbose($"[dim]{extensionsDll}[/]");
        }

        string? webResourcesSyncFolder = null;
        if (pushWebResources && standaloneMode)
        {
            webResourcesSyncFolder = standaloneWebResourcesPath;
        }
        else if (pushWebResources)
        {
            var webResourcesFolder = Path.Combine(RootFolder, AllSolutionsFolderName, solutionName!, WebResourcesName);
            webResourcesSyncFolder = Path.Combine(webResourcesFolder, "dist");
            if (Directory.Exists(webResourcesFolder))
            {
                if (await DotNetUtils.BuildSolutionAsync(webResourcesFolder, DotnetBuild.Release, settings.Verbose, cancellationToken) != 0)
                {
                    return 1;
                }
            }
            else
            {
                AnsiConsole.MarkupLine("[dim]WebResources project not found — skipping[/]");
                pushWebResources = false;
            }
        }

        // Find PAC profile and connect
        IOrganizationServiceAsync2? conn = AnsiConsole.Status().FlowlineSpinner().Start(
            "Connecting to Dataverse...",
            ctx =>
            {
                var profile = dataverseConnector.GetPacProfiles()
                                     .FirstOrDefault(p => p.Resource?.TrimEnd('/').Equals(environmentUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) == true)
                              ?? dataverseConnector.GetPacProfiles().FirstOrDefault(p => p.IsUniversal);

                if (profile != null) return dataverseConnector.ConnectViaPac(profile, environmentUrl);

                AnsiConsole.MarkupLine("[red]No PAC profile found — run 'pac auth create' first.[/]");
                return null;

            });

        if (conn == null) return 1;
        AnsiConsole.MarkupLine("[green]Connected[/]");

        if (pushPlugins && extensionsDll != null)
        {
            await AnsiConsole.Status().FlowlineSpinner().StartAsync(
                $"Pushing [bold]{Path.GetFileName(extensionsDll)}[/]...",
                ctx => pluginService.SyncSolutionAsync(conn, extensionsDll, solutionName!, runMode, cancellationToken));
        }

        if (pushWebResources)
        {
            await AnsiConsole.Status().FlowlineSpinner().StartAsync(
                "Pushing web resources...",
                ctx => webResourceService.SyncSolutionAsync(conn, webResourcesSyncFolder!, solutionName!, runMode: runMode, cancellationToken: cancellationToken));
        }

        AnsiConsole.MarkupLine("[green]Assets pushed[/]");

        AnsiConsole.MarkupLine("[bold green]:rocket: Pushed! Use 'sync' to keep it in flow.[/]");

        return 0;
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

    internal static bool ValidateStandaloneMode(Settings settings, string rootFolder)
    {
        if (settings.Scopes.Length > 0)
        {
            AnsiConsole.MarkupLine("[red]--scope cannot be used together with --dll or --webresources. Use either project mode or standalone artifact mode.[/]");
            return false;
        }

        if (File.Exists(Path.Combine(rootFolder, ProjectConfig.s_configFileName)))
        {
            AnsiConsole.MarkupLine("[red]--dll and --webresources cannot be used inside a Flowline project folder. Use project mode or run standalone push from another folder.[/]");
            return false;
        }

        return true;
    }

    internal static string? ResolveStandaloneSolutionName(Settings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Solution))
            return settings.Solution.Trim();

        AnsiConsole.MarkupLine("[red]Solution name is required in standalone mode — pass it as the first argument.[/]");
        return null;
    }

    internal static string? ResolveStandaloneEnvironmentUrl(Settings settings, DataverseConnector dataverseConnector)
    {
        if (!string.IsNullOrWhiteSpace(settings.DevUrl))
            return settings.DevUrl.Trim();

        var profile = dataverseConnector.GetCurrentResourceSpecificPacProfile();
        if (!string.IsNullOrWhiteSpace(profile?.Resource))
            return profile.Resource.Trim();

        AnsiConsole.MarkupLine("[red]Dev URL is required in standalone mode — use --dev <URL> or select a resource-specific PAC profile.[/]");
        return null;
    }

    internal static string? ResolveStandaloneDllPath(Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Dll))
            return null;

        var path = Path.GetFullPath(settings.Dll);
        if (!string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[red]--dll must point to a .dll file.[/]");
            return null;
        }

        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]DLL not found: {path}[/]");
            return null;
        }

        return path;
    }

    internal static string? ResolveStandaloneWebResourcesPath(Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.WebResources))
            return null;

        var path = Path.GetFullPath(settings.WebResources);
        if (!Directory.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]Web resources folder not found: {path}[/]");
            return null;
        }

        return path;
    }

    static async Task<SolutionInfo?> GetAndCheckStandaloneSolutionAsync(
        string solutionName,
        string environmentUrl,
        Settings settings,
        CancellationToken cancellationToken)
    {
        List<SolutionInfo> solutions = await AnsiConsole.Status().FlowlineSpinner().StartAsync(
            $"Looking up [bold]{solutionName}[/]...",
            ctx => PacUtils.GetSolutionsAsync(environmentUrl, settings.Verbose, cancellationToken));

        var remoteSln = solutions.FirstOrDefault(s => s.SolutionUniqueName?.Equals(solutionName, StringComparison.OrdinalIgnoreCase) == true);
        if (remoteSln == null)
        {
            AnsiConsole.MarkupLine($"[red]'{solutionName}' not found in that environment.[/]");
            return null;
        }

        AnsiConsole.MarkupLine($"[green]Solution: [bold]{solutionName}[/] (managed: {remoteSln.IsManaged})[/]");
        return remoteSln;
    }

    static async Task<EnvironmentInfo?> GetAndCheckStandaloneEnvironmentAsync(
        string environmentUrl,
        Settings settings,
        CancellationToken cancellationToken)
    {
        EnvironmentInfo? env = await AnsiConsole.Status().FlowlineSpinner().StartAsync(
            $"Checking dev [bold]{environmentUrl}[/]...",
            ctx => PacUtils.GetEnvironmentInfoByUrlAsync(environmentUrl, settings.Verbose, cancellationToken));

        if (env == null)
        {
            AnsiConsole.MarkupLine("[red]Dev environment not found — check the URL or your PAC login.[/]");
            return null;
        }

        if (env.Type == "Production")
        {
            AnsiConsole.MarkupLine("[red]That's a Production environment — use a sandbox or dev instead.[/]");
            return null;
        }

        AnsiConsole.MarkupLine($"[green]Dev: [bold]{env.DisplayName}[/] ({env.EnvironmentUrl})[/]");
        return env;
    }
}
