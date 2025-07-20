using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class DeleteEnvCommandSettings : BaseCommandSettings
{
    [CommandArgument(0, "<environment>")]
    [Description("The Power Platform environment to clone")]
    public string Environment { get; set; } = null!;
}

public class DeleteEnvCommand : AsyncCommand<DeleteEnvCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, DeleteEnvCommandSettings settings)
    {
        AnsiConsole.MarkupLine($"Running command [green]'delete-env'[/] for environment [green]'{settings.Environment}'[/]...");

        await PacUtils.AssertPacCliInstalledAsync();

        AnsiConsole.MarkupLine("Deleting environment...");
        // TODO: Implement the delete-env logic

        AnsiConsole.MarkupLine("[green]All done![/]");

        return 0;
    }
}
