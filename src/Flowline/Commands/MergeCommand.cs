using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class MergeCommandSettings : BaseCommandSettings
{
    [CommandArgument(0, "<environment>")]
    [Description("The Power Platform environment to clone")]
    public string Environment { get; set; } = null!;
}

public class MergeCommand : AsyncCommand<MergeCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, MergeCommandSettings settings)
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
