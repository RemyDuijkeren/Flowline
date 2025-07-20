using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class DeployCommandSettings : BaseCommandSettings
{
    [CommandOption("-e|--environment")]
    [Description("The environment to run the command against")]
    [DefaultValue("https://your-environment-url.crm.dynamics.com/")]
    public string Environment { get; set; } = "https://your-environment-url.crm.dynamics.com/";

    [CommandOption("-s|--solution")]
    [Description("The solution name to sync")]
    [DefaultValue("Cr07982")]
    public string SolutionName { get; set; } = "Cr07982";
}

public class DeployCommand : AsyncCommand<DeployCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, DeployCommandSettings settings)
    {
        AnsiConsole.MarkupLine($"Running command [green]'deploy'[/] for environment [green]'{settings.Environment}'[/]...");

        await PacUtils.AssertPacCliInstalledAsync();
        await PacUtils.AssertGitInstalledAsync();

        AnsiConsole.MarkupLine("Pushing changes to test environment...");
        // TODO: Implement the deploy logic

        AnsiConsole.MarkupLine("[green]All done![/]");

        return 0;
    }
}
