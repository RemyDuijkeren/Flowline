using System.ComponentModel;
using CliWrap;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class InitCommandSettings : FlowlineCommandSettings
{
    [CommandOption("-s|--solution")]
    [Description("The solution name to initialize")]
    [DefaultValue("Cr07982")]
    public string SolutionName { get; set; } = "Cr07982";

    [CommandOption("-r|--repo")]
    [Description("Git repository URL")]
    [DefaultValue("https://github.com/AutomateValue/Dataverse01.git")]
    public string GitRemoteUrl { get; set; } = "https://github.com/AutomateValue/Dataverse01.git";
}

public class InitCommand : AsyncCommand<InitCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, InitCommandSettings settings)
    {
        await PacUtils.AssertPacCliInstalledAsync();
        await PacUtils.AssertGitInstalledAsync();

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
        else
        {
            AnsiConsole.MarkupLine("[yellow]Git repository already initialized.[/]");
        }

        // Clone solution from Dataverse if it doesn't exist locally
        if (!File.Exists(cdsprojPath))
        {
            AnsiConsole.MarkupLine($"No solution folder for '{settings.SolutionName}' found. Cloning from Dataverse...");

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
            AnsiConsole.MarkupLine("[yellow]Solution already exists locally.[/]");
        }

        AnsiConsole.MarkupLine("[green]Initialization complete! You can now use 'sync' to keep your solution up to date.[/]");

        return 0;
    }
}
