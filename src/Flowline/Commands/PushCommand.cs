using System.ComponentModel;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using Flowline.Utils;

namespace Flowline.Commands;

public class PushCommand(IAuthenticationService authSrv, IPluginSyncService pluginSyncSrv)
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
        [Description("The name of the solution to push")]
        public string? Solution { get; set; }

        [CommandOption("-s|--scope <SCOPE>")]
        [Description("limit the push scope (all|webresources|plugins|pcf). Can be specified multiple times.")]
        [DefaultValue(new[] { PushScope.All })]
        public PushScope[] Scopes { get; set; } = [PushScope.All];

        [CommandOption("--dev <url>")]
        [Description("Override the configured development environment")]
        public string? DevUrl { get; set; }

        [CommandOption("--save")]
        [Description("Don't delete assets in development environment that are not in the source control")]
        public bool Save { get; set; } = false;
    }

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Dev URL is required for push
        var devEnv = await GetAndCheckEnvironmentInfoAsync(EnvironmentRole.Dev, settings.DevUrl, settings, cancellationToken);
        if (devEnv == null) return 1;

        // Resolve solution
        var sln = await GetAndCheckSolutionAsync(settings.Solution, devEnv.EnvironmentUrl!, false, settings, cancellationToken);
        if (sln == null) return 1;

        // Resolve Scopes into one final scope (PushScope finalScope)
        var pushScope = settings.Scopes.Aggregate(PushScope.None, (current, scope) => current | scope);
        AnsiConsole.MarkupLine($"[dim]Push scope: {pushScope}[/]");

        // Resolve scopes
        AnsiConsole.MarkupLine($"Pushing assets [bold]{pushScope}[/] for solution [bold]{sln.Name}[/] to environment [bold]'{devEnv.EnvironmentUrl}'[/]...");

        // Build the solution in dotnet
        var extensionsFolder = Path.Combine(RootFolder, AllSolutionsFolderName, sln.Name, ExtensionsName);
        if (await DotNetUtils.BuildSolutionAsync(extensionsFolder, DotnetBuild.Release, settings.Verbose, cancellationToken) != 0)
        {
            return 1;
        }

        // Find 'Extensions.dll' in bin/Release folder
        var extensionsDll = Path.Combine(extensionsFolder, "bin", "Release", "net462", "publish", $"{ExtensionsName}.dll");
        if (!File.Exists(extensionsDll))
        {
            AnsiConsole.MarkupLine("[red]Extensions.dll not found. Please build the solution first.[/]");
            return 1;
        }

        // Sync Plugins
        var profile = authSrv.GetPacProfiles()
                             .FirstOrDefault(p => p.Resource?.TrimEnd('/').Equals(devEnv.EnvironmentUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) == true)
                      ?? authSrv.GetPacProfiles().FirstOrDefault(p => p.IsUniversal);
        if (profile == null)
        {
            AnsiConsole.MarkupLine("[red]No PAC profile found — run 'pac auth create' first.[/]");
            return 1;
        }

        var conn = authSrv.ConnectViaPac(profile, devEnv.EnvironmentUrl);
        await pluginSyncSrv.SyncSolutionAsync(conn, extensionsDll, sln.Name, IsolationMode.Sandbox);

        if (settings.Save) AnsiConsole.MarkupLine("[dim]Save mode enabled: Assets not in source control will be preserved.[/]");
        if (settings.Force) AnsiConsole.MarkupLine("[dim]Force mode enabled: Safety checks will be bypassed.[/]");

        AnsiConsole.MarkupLine("[green]All done![/]");

        return 0;
    }
}
