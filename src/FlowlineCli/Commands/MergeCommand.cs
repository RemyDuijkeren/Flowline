using Spectre.Console;
using Spectre.Console.Cli;

namespace FlowLineCli.Commands;

public class MergeCommand : AsyncCommand<FlowlineCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, FlowlineCommandSettings settings)
    {
        AnsiConsole.MarkupLine($"Running command [green]'merge'[/] for environment [green]'{settings.Environment}'[/]...");

        await PacUtils.AssertPacCliInstalledAsync();
        await PacUtils.AssertGitInstalledAsync();

        AnsiConsole.MarkupLine("Merge pull request into master...");
        // TODO: Implement the merge logic

        AnsiConsole.MarkupLine("[green]All done![/]");

        return 0;
    }
}
