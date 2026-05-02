using System.ComponentModel;
using CliWrap;
using Flowline.Core;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using Flowline.Utils;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace Flowline.Commands;

public class PushCommand(AuthenticationService authSrv, AssemblyAnalysisService analysisService, PluginRegistrationService pluginSyncSrv, AnsiConsoleOutput output)
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
        [DefaultValue(new[] { PushScope.All })]
        public PushScope[] Scopes { get; set; } = [PushScope.All];

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

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        output.IsVerbose = settings.Verbose;
        var runMode = settings.DryRun ? RunMode.DryRun
                    : settings.Save   ? RunMode.Save
                    : RunMode.Normal;
        if (runMode == RunMode.Save)   AnsiConsole.MarkupLine("[dim]Save mode: missing source assets stay in Dataverse[/]");
        if (runMode == RunMode.DryRun) AnsiConsole.MarkupLine("[dim]Dry run: preview only[/]");
        if (settings.Force) AnsiConsole.MarkupLine("[dim]Force mode: safety checks off[/]");

        // Dev URL is required for push
        var devEnv = await GetAndCheckEnvironmentInfoAsync(EnvironmentRole.Dev, settings.DevUrl, settings, cancellationToken);
        if (devEnv == null) return 1;

        // Resolve solution
        var sln = await GetAndCheckSolutionAsync(settings.Solution, devEnv.EnvironmentUrl!, false, settings, cancellationToken);
        if (sln == null) return 1;

        // Resolve Scopes into one final scope (PushScope finalScope)
        var pushScope = settings.Scopes.Aggregate(PushScope.None, (current, scope) => current | scope);
        AnsiConsole.MarkupLine($"[dim]Scope: {pushScope}[/]");

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
            AnsiConsole.MarkupLine("[red]Extensions.dll not found. Build the solution first.[/]");
            return 1;
        }
        AnsiConsole.MarkupLine("[green][bold]Extensions.dll[/] found[/]");
        output.Verbose($"[dim]{extensionsDll}[/]");

        // Analyze the assembly
        var metadata = AnsiConsole.Status().FlowlineSpinner().Start(
            "Reading [bold]Extensions.dll[/]...",
            ctx => analysisService.Analyze(extensionsDll));

        AnsiConsole.MarkupLine("[green]Metadata ready[/]");

        // Find PAC profile and connect
        IOrganizationServiceAsync2? conn = AnsiConsole.Status().FlowlineSpinner().Start(
            "Connecting to Dataverse...",
            ctx =>
            {
                var profile = authSrv.GetPacProfiles()
                                     .FirstOrDefault(p => p.Resource?.TrimEnd('/').Equals(devEnv.EnvironmentUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) == true)
                              ?? authSrv.GetPacProfiles().FirstOrDefault(p => p.IsUniversal);

                if (profile != null) return authSrv.ConnectViaPac(profile, devEnv.EnvironmentUrl);

                AnsiConsole.MarkupLine("[red]No PAC profile found — run 'pac auth create' first.[/]");
                return null;

            });

        if (conn == null) return 1;
        AnsiConsole.MarkupLine("[green]Connected[/]");

        await AnsiConsole.Status().FlowlineSpinner().StartAsync(
            $"Pushing [bold]{ExtensionsName}.dll[/]...",
            ctx => pluginSyncSrv.SyncAsync(conn, metadata, sln.Name, runMode, cancellationToken));

        AnsiConsole.MarkupLine("[green]Assets pushed[/]");

        AnsiConsole.MarkupLine("[bold green]:rocket: Pushed! Use 'sync' to keep it in flow.[/]");

        return 0;
    }
}
