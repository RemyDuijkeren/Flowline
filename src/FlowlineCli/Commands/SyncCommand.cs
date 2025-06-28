using System.ComponentModel;
using CliWrap;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FlowLineCli.Commands;

public class SyncCommandSettings : FlowlineCommandSettings
{
    [CommandOption("-s|--solution")]
    [Description("The solution name to sync")]
    [DefaultValue("Cr07982")]
    public string SolutionName { get; set; } = "Cr07982";

    [CommandOption("-r|--repo")]
    [Description("Git repository URL")]
    [DefaultValue("https://github.com/AutomateValue/Dataverse01.git")]
    public string GitRemoteUrl { get; set; } = "https://github.com/AutomateValue/Dataverse01.git";

    [CommandOption("-m|--message")]
    [Description("Commit message")]
    public string? CommitMessage { get; set; }
}

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

        // Clone Git repo if not already a Git repo
        if (!Directory.Exists(Path.Combine(rootFolder, ".git")))
        {
            AnsiConsole.MarkupLine("No repository found. Cloning...");

            var result = await Cli.Wrap("git")
                .WithArguments($"clone {settings.GitRemoteUrl} {rootFolder}")
                .ExecuteAsync();

            if (result.ExitCode != 0)
            {
                AnsiConsole.MarkupLine("[red]Failed to clone the repository. Please check the URL and your network connection.[/]");
                return 1;
            }
        }

        if (!File.Exists(cdsprojPath))
        {
            AnsiConsole.MarkupLine($"No solution folder for '{settings.SolutionName}' found. Cloning...");

            if (Directory.Exists(srcSolutionFolder))
            {
                AnsiConsole.MarkupLine("Removing existing solution folder...");
                Directory.Delete(srcSolutionFolder, true);
            }

            var result = await Cli.Wrap("pac")
                .WithArguments($"solution clone --name {settings.SolutionName} --environment {settings.Environment} --packagetype Unmanaged --outputDirectory \"{Path.Combine(rootFolder, "src")}\"")
                .ExecuteAsync();

            if (result.ExitCode != 0)
            {
                AnsiConsole.MarkupLine("[red]Failed to clone the solution. Please check the environment and solution name.[/]");
                return 1;
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"The solution folder for '{settings.SolutionName}' exists! Syncing it...");

            var result = await Cli.Wrap("pac")
                .WithArguments($"solution sync --solution-folder {srcSolutionFolder} --environment {settings.Environment} --packagetype Unmanaged")
                .ExecuteAsync();

            if (result.ExitCode != 0)
            {
                AnsiConsole.MarkupLine("[red]Failed to sync the solution. Please check the environment and solution name.[/]");
                return 1;
            }
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
