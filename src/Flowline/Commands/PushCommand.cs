using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Flowline.Config;
using Flowline.Utils;

namespace Flowline.Commands;

public class PushCommand : AsyncCommand<PushCommand.Settings>
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

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        await DotNetUtils.AssertDotNetInstalledAsync(settings.Verbose, cancellationToken);
        await PacUtils.AssertPacCliInstalledAsync(settings.Verbose, cancellationToken);
        await GitUtils.AssertGitInstalledAsync(settings.Verbose, cancellationToken);

        var rootFolder = Directory.GetCurrentDirectory();
        await GitUtils.AssertGitRepoAsync(rootFolder, settings.Verbose, cancellationToken);

        // Load project configuration
        var config = ProjectConfig.Load();
        if (config == null)
        {
            AnsiConsole.MarkupLine("[red]No project configuration found. Please run 'clone' first.[/]");
            return 1;
        }

        // Resolve solution
        var solutionName = settings.Solution;
        var sln = config.GetOrUpdateSolution(solutionName, false, settings);
        if (sln == null)
        {
            AnsiConsole.MarkupLine("[red]Solution name is required. Please provide a solution name or configure it in .flowline.[/]");
            return 1;
        }

        // Resolve Scopes into one final scope (PushScope finalScope)
        var pushScope = settings.Scopes.Aggregate(PushScope.None, (current, scope) => current | scope);
        AnsiConsole.MarkupLine($"[dim]Push scope: {pushScope}[/]");

        // Dev URL is required for push
        var devUrl = config.GetOrUpdateDevUrl(settings.DevUrl, settings);
        if (string.IsNullOrEmpty(devUrl))
        {
            AnsiConsole.MarkupLine("[red]Dev URL is required for push. Please provide a dev URL using --dev <url> or configure it in .flowline.[/]");
            return 1;
        }

        // Validate Dev environment
        AnsiConsole.MarkupLine($"Validating [bold]'{devUrl}'[/]...");
        var devEnv = await PacUtils.GetEnvironmentInfoByUrlAsync(devUrl, settings.Verbose, cancellationToken);
        if (devEnv == null)
        {
            AnsiConsole.MarkupLine("[red]Invalid Dev environment. Please provide a valid Dataverse environment URL.[/]");
            return 1;
        }

        if (devEnv.Type == "Production")
        {
            AnsiConsole.MarkupLine("[red]Push is a dev-only workflow and cannot be used with Production environments.[/]");
            return 1;
        }

        // Resolve scopes
        AnsiConsole.MarkupLine($"Pushing assets [bold]{pushScope}[/] for solution [bold]{sln.Name}[/] to [bold]{devEnv.DisplayName}[/]...");

        // TODO: Implement the upload logic
        if (settings.Save) AnsiConsole.MarkupLine("[dim]Save mode enabled: Assets not in source control will be preserved.[/]");
        if (settings.Force) AnsiConsole.MarkupLine("[dim]Force mode enabled: Safety checks will be bypassed.[/]");

        AnsiConsole.MarkupLine("[green]All done![/]");

        return 0;
    }
}
