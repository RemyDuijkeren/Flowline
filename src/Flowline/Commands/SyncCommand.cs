using System.ComponentModel;
using CliWrap;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class SyncCommandSettings : FlowlineCommandSettings
{
    [CommandOption("-s|--solution")]
    [Description("The solution name to sync")]
    [DefaultValue("Cr07982")]
    public string SolutionName { get; set; } = "Cr07982";

    [CommandOption("-m|--message")]
    [Description("Commit message")]
    public string? CommitMessage { get; set; }
}

// Modified SyncCommand.cs
public class SyncCommand : AsyncCommand<SyncCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, SyncCommandSettings settings)
    {
        await PacUtils.AssertPacCliInstalledAsync();
        await PacUtils.AssertGitInstalledAsync();

        var commitMessage = settings.CommitMessage ?? $"Commit changes to solution '{settings.SolutionName}' in environment '{settings.Environment}'";
        var rootFolder = Directory.GetCurrentDirectory();
        var srcSolutionFolder = Path.Combine(rootFolder, "src", settings.SolutionName);
        var cdsprojPath = Path.Combine(srcSolutionFolder, $"{settings.SolutionName}.cdsproj");

        // Check if we're in an initialized environment
        if (!Directory.Exists(Path.Combine(rootFolder, ".git")))
        {
            AnsiConsole.MarkupLine("[red]This directory is not a git repository. Please run 'init' first.[/]");
            return 1;
        }

        if (!File.Exists(cdsprojPath))
        {
            AnsiConsole.MarkupLine($"[red]Solution '{settings.SolutionName}' not found. Please run 'init' first.[/]");
            return 1;
        }

        // Perform sync
        AnsiConsole.MarkupLine($"Syncing solution '{settings.SolutionName}'...");

        var result = await Cli.Wrap("pac")
                              .WithArguments(
                                  $"solution sync --solution-folder {srcSolutionFolder} --environment {settings.Environment} --packagetype Unmanaged")
                              .ExecuteAsync();

        if (result.ExitCode != 0)
        {
            AnsiConsole.MarkupLine("[red]Failed to sync the solution. Please check the environment and solution name.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"Building Solution '{settings.SolutionName}'...");

        await Cli.Wrap("dotnet")
                 .WithArguments($"build {srcSolutionFolder} --output \"{Path.Combine(rootFolder, "artifacts")}\"")
                 .ExecuteAsync();

        AnsiConsole.MarkupLine("Committing changes to local repository...");

        await Cli.Wrap("git")
                 .WithArguments("add -A")
                 .ExecuteAsync();

        await Cli.Wrap("git")
                 .WithArguments($"commit -m \"{commitMessage}\"")
                 .ExecuteAsync();

        AnsiConsole.MarkupLine("Pushing changes to remote repository...");

        await Cli.Wrap("git")
                 .WithArguments("push")
                 .ExecuteAsync();

        AnsiConsole.MarkupLine("[green]All done![/]");

        return 0;
    }
}
