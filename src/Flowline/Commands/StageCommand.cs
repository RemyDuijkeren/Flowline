using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class StageCommand : AsyncCommand<StageCommand.Settings>
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandOption("-s|--solution")]
        [Description("The solution name to sync")]
        [DefaultValue("Cr07982")]
        public string SolutionName { get; set; } = "Cr07982";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        //AnsiConsole.MarkupLine($"Running command [green]'deploy'[/] for environment [green]'{settings.Environment}'[/]...");

        await PacUtils.AssertPacCliInstalledAsync();
        await GitUtils.AssertGitInstalledAsync();

        AnsiConsole.MarkupLine("Pushing changes to test environment...");
        // TODO: Implement the deploy logic

        AnsiConsole.MarkupLine("[green]All done![/]");

        return 0;
    }
}
