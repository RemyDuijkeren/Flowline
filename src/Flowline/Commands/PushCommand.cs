using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class PushCommand : AsyncCommand<PushCommand.Settings>
{
    public sealed class Settings : FlowlineSettings
    {
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        await PacUtils.AssertPacCliInstalledAsync(cancellationToken);
        await GitUtils.AssertGitInstalledAsync(cancellationToken);

        AnsiConsole.MarkupLine("Push to dev environment...");
        // TODO: Implement the upload logic

        AnsiConsole.MarkupLine("[green]All done![/]");

        return 0;
    }
}
