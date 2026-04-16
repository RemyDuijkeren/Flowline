using System.ComponentModel;
using System.Diagnostics;
using CliWrap;
using Spectre.Console;
using Spectre.Console.Cli;
using Flowline.Config;
using Flowline.Utils;

namespace Flowline.Commands;

public class PushCommand : FlowlineCommand<PushCommand.Settings>
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
        var devEnv = await ResolveAndValidateDevUrlAsync(settings.DevUrl, settings, cancellationToken);
        if (devEnv == null) return 1;

        // Resolve solution
        var sln = await ResolveAndValidateSolutionAsync(settings.Solution, devEnv.EnvironmentUrl!, false, settings, cancellationToken);
        if (sln == null) return 1;

        // Resolve Scopes into one final scope (PushScope finalScope)
        var pushScope = settings.Scopes.Aggregate(PushScope.None, (current, scope) => current | scope);
        AnsiConsole.MarkupLine($"[dim]Push scope: {pushScope}[/]");

        // Resolve scopes
        AnsiConsole.MarkupLine($"Pushing assets [bold]{pushScope}[/] for solution [bold]{sln.Name}[/] to environment [bold]'{devEnv.EnvironmentUrl}'[/]...");

        // Build the solution in dotnet
        var slnFolder = Path.Combine(RootFolder, AllSolutionsFolderName, sln.Name);
        if (await DotNetUtils.BuildSolutionAsync(slnFolder, settings.Verbose, cancellationToken) != 0)
        {
            return 1;
        }

        // Push Extensions (plugins) to Dev environment
        var extensionsFolder = Path.Combine(slnFolder, ExtensionsName);
        var extensionsCsproj = Path.Combine(extensionsFolder, $"{ExtensionsName}.csproj");

        var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);
        CommandResult result = await AnsiConsole.Status().FlowlineSpinner().StartAsync(
            "Connecting...",
            ctx => Cli.Wrap(cmdName)
                      .WithArguments(args => args
                                             .AddIfNotNull(prefixArgs)
                                             .Add("plugin")
                                             .Add("push")
                                             .Add("--name").Add(sln.Name)
                                             .Add("--environment").Add(devEnv.EnvironmentUrl!)
                                             .Add("--packagetype").Add(sln.IncludeManaged ? "Both" : "Unmanaged")
                                             .Add("--outputDirectory").Add(slnFolder) // will create <sln.Name> folder under this given folder
                                             .Add("--async"))
                      .WithValidation(CommandResultValidation.None)
                      .WithToolExecutionLog(settings.Verbose, ctx)
                      .ExecuteAsync(cancellationToken)
                      .Task);

        if (!result.IsSuccess)
        {
            AnsiConsole.MarkupLine("[red]Failed to clone the solution. Please check the environment and solution name.[/]");
            return 1;
        }


        // TODO: Implement the upload logic
        if (settings.Save) AnsiConsole.MarkupLine("[dim]Save mode enabled: Assets not in source control will be preserved.[/]");
        if (settings.Force) AnsiConsole.MarkupLine("[dim]Force mode enabled: Safety checks will be bypassed.[/]");

        AnsiConsole.MarkupLine("[green]All done![/]");

        return 0;
    }
}
