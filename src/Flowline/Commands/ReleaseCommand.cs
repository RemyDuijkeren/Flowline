using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class ReleaseCommand : AsyncCommand<ReleaseCommand.Settings>
{
    public sealed class Settings : FlowlineSettings
    {
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        //AnsiConsole.MarkupLine($"Running command [green]'merge'[/] for environment [green]'{settings.Environment}'[/]...");

        await PacUtils.AssertPacCliInstalledAsync();
        await GitUtils.AssertGitInstalledAsync();

        AnsiConsole.MarkupLine("Merge pull request into master...");
        // TODO: Implement the merge logic

        AnsiConsole.MarkupLine("[green]All done![/]");

        return 0;
    }
}
