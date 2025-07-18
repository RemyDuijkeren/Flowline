using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class DeleteEnvCommand : AsyncCommand<FlowlineCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, FlowlineCommandSettings settings)
    {
        AnsiConsole.MarkupLine($"Running command [green]'delete-env'[/] for environment [green]'{settings.Environment}'[/]...");

        await PacUtils.AssertPacCliInstalledAsync();

        AnsiConsole.MarkupLine("Deleting environment...");
        // TODO: Implement the delete-env logic

        AnsiConsole.MarkupLine("[green]All done![/]");

        return 0;
    }
}
