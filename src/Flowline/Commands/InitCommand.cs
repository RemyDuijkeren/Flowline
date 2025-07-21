using System.ComponentModel;
using CliWrap;
using Flowline.Config;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class InitCommandSettings : BaseCommandSettings
{
    [CommandArgument(0, "<environment>")]
    [Description("The environment to run the command against")]
    public string Environment { get; set; } = null!; // = "https://automatevalue-dev.crm4.dynamics.com/";

    [CommandArgument(1, "<repo-url>")]
    [Description("Git repository URL")]
    public string GitRemoteUrl { get; set; } = null!; // = "https://github.com/AutomateValue/Dataverse01.git";

    [CommandOption("-s|--solution")]
    [Description("The solution name to initialize")]
    [DefaultValue("Cr07982")]
    public string SolutionName { get; set; } = "Cr07982";

    [CommandOption("--managed")]
    [Description("Use managed solution instead of unmanaged")]
    public bool Managed { get; set; } = false;
}

public class InitCommand : AsyncCommand<InitCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, InitCommandSettings settings)
    {
        await PacUtils.AssertGitInstalledAsync();

        var rootFolder = Directory.GetCurrentDirectory();
        var srcSolutionFolder = Path.Combine(rootFolder, "src", settings.SolutionName);
        var cdsprojPath = Path.Combine(srcSolutionFolder, $"{settings.SolutionName}.cdsproj");

        // Clone Git repo if not already a Git repo
        if (!Directory.Exists(Path.Combine(rootFolder, ".git")))
        {
            AnsiConsole.MarkupLine("No Git repository found. Cloning...");

            var result = await Cli.Wrap("git")
                .WithArguments($"clone {settings.GitRemoteUrl} {rootFolder}")
                .ExecuteAsync();

            if (result.ExitCode != 0)
            {
                AnsiConsole.MarkupLine("[red]Failed to clone the repository. Please check the Git URL and your network connection.[/]");
                return 1;
            }
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]Git repository already initialized.[/]");
        }

        // Clone solution from Dataverse if it doesn't exist locally
        await PacUtils.AssertPacCliInstalledAsync();

        if (!File.Exists(cdsprojPath))
        {
            AnsiConsole.MarkupLine($"No solution folder for '{settings.SolutionName}' found. Cloning from Dataverse...");

            if (Directory.Exists(srcSolutionFolder))
            {
                AnsiConsole.MarkupLine("Removing existing solution folder...");
                Directory.Delete(srcSolutionFolder, true);
            }

            var result = await Cli.Wrap("pac")
                   .WithArguments(args => args
                       .Add("solution")
                       .Add("clone")
                       .Add("--name").Add(settings.SolutionName)
                       .Add("--environment").Add(settings.Environment)
                       .Add("--packagetype").Add(settings.Managed ? "Both" : "Unmanaged")
                       .Add("--outputDirectory").Add($"{Path.Combine(rootFolder, "src")}"))
                   .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]PAC: {s}[/]")))
                   .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
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

        // Determine if this is a production or development environment
        var environments = await PacUtils.GetEnvironmentsAsync();
        var env = environments.FirstOrDefault(e => e.EnvironmentUrl?.Contains(settings.Environment) == true);
        bool isProd = env?.Type == "Production";

        // Load existing config (in case we already have other environments configured)
        var config = ProjectConfig.Load();

        // Update config with new environment info
        config.SetEnvironment(settings.Environment, isProd);
        config.SolutionName = settings.SolutionName;
        config.UseManagedSolution = settings.Managed;
        config.Save();

        AnsiConsole.MarkupLine("[green]Initialization complete! You can now use 'sync' to keep your solution up to date.[/]");
        AnsiConsole.MarkupLine("[dim]Project configuration saved. You can now run 'sync' without specifying the environment.[/]");

        return 0;
    }
}
