using Spectre.Console;
using Spectre.Console.Cli;

namespace FlowLineCli.Commands;

public class PushToTestCommand : AsyncCommand<FlowlineCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, FlowlineCommandSettings settings)
    {
        AnsiConsole.MarkupLine($"Running command [green]'push-to-test'[/] for environment [green]'{settings.Environment}'[/]...");

        await PacUtils.AssertPacCliInstalledAsync();
        await PacUtils.AssertGitInstalledAsync();

        AnsiConsole.MarkupLine("Pushing changes to test environment...");
        // TODO: Implement the push-to-test logic

        AnsiConsole.MarkupLine("[green]All done![/]");

        return 0;
    }
}
