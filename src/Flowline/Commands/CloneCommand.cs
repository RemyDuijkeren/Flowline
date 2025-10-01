using System.ComponentModel;
using CliWrap;
using Flowline.Config;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class CloneCommandSettings : BaseCommandSettings
{
    [CommandArgument(0, "<repo-url>")]
    [Description("Git repository URL")]
    public string GitRemoteUrl { get; set; } = null!; // = "https://github.com/AutomateValue/Dataverse01.git";

    [CommandArgument(1, "[environment]")]
    [Description("The environment to run the command against")]
    public string? Environment { get; set; } // = "https://automatevalue-dev.crm4.dynamics.com/";

    [CommandOption("-s|--solution")]
    [Description("The solution name to initialize")]
    [DefaultValue("Cr07982")]
    public string? SolutionName { get; set; } = "Cr07982";

    [CommandOption("--managed")]
    [Description("Use managed solution instead of unmanaged")]
    public bool Managed { get; set; } = false;
}

public class CloneCommand : AsyncCommand<CloneCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CloneCommandSettings settings)
    {
        await GitUtils.AssertGitInstalledAsync();
        await PacUtils.AssertPacCliInstalledAsync();

        // Clone Git repo if not already a Git repo
        var rootFolder = Directory.GetCurrentDirectory();
        if (!Directory.Exists(Path.Combine(rootFolder, ".git")))
        {
            AnsiConsole.MarkupLine("No Git repository found. Cloning...");

            var result = await Cli.Wrap("git")
                                  .WithArguments(args => args
                                                         .Add("clone")
                                                         .Add(settings.GitRemoteUrl)
                                                         .Add(rootFolder))
                                  .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]GIT: {s}[/]")))
                                  .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
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

        // Load project configuration if exists
        var config = ProjectConfig.Load();
        if (config != null)
        {
            AnsiConsole.MarkupLine("[yellow]Project configuration already exists.[/]");
            if (string.IsNullOrEmpty(settings.Environment))
            {
                settings.Environment = config.GetCurrentEnvironment();
                if (string.IsNullOrEmpty(settings.Environment))
                {
                    AnsiConsole.MarkupLine(
                        "[red]No environment configured. Please provide an environment URL using -e <environment> or --environment <environment>.[/]");
                    return 1;
                }
            }
            else if (config.GetCurrentEnvironment() != settings.Environment)
            {
                AnsiConsole.MarkupLine("[yellow]Overriding existing environment project configuration.[/]");
                var srcEnvironment = await PacUtils.GetEnvironmentByUrlAsync(settings.Environment);
                if (srcEnvironment == null)
                {
                    AnsiConsole.MarkupLine("[red]Invalid environment URL. Please provide a valid Dataverse environment URL.[/]");
                    return 1;
                }
                else
                {
                    AnsiConsole.MarkupLine(
                        $"Using environment: [bold]{srcEnvironment.DisplayName}[/] ({srcEnvironment.EnvironmentUrl}) - Type: {srcEnvironment.Type})");
                    config.SetEnvironment(settings.Environment, srcEnvironment.Type == "Production");
                }
            }

            if (string.IsNullOrEmpty(settings.SolutionName))
            {
                settings.SolutionName = config.SolutionName;
                AnsiConsole.MarkupLine($"Using existing solution name: [bold]{settings.SolutionName}[/]");
            }
            else if (config.SolutionName != settings.SolutionName)
            {
                AnsiConsole.MarkupLine("[yellow]Overriding existing solution name in project configuration.[/]");
                config.SolutionName = settings.SolutionName;
            }

            if (config.UseManagedSolution != settings.Managed)
            {
                AnsiConsole.MarkupLine("[yellow]Overriding existing solution type in project configuration.[/]");
                config.UseManagedSolution = settings.Managed;
            }

            config.Save();
        }
        else
        {
            AnsiConsole.MarkupLine("No project configuration found. Creating a new one...");
            config = new ProjectConfig();

            // Environment exists?
            if (string.IsNullOrEmpty(settings.Environment))
            {
                AnsiConsole.MarkupLine(
                    "[red]Environment URL is required. Please provide a Dataverse environment URL using -e <environment> or --environment <environment>.[/]");
                return 1;
            }

            var sourceEnvironment = await PacUtils.GetEnvironmentByUrlAsync(settings.Environment);
            if (sourceEnvironment == null)
            {
                AnsiConsole.MarkupLine("[red]Invalid environment URL. Please provide a valid Dataverse environment URL.[/]");
                return 1;
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"Using environment: [bold]{sourceEnvironment.DisplayName}[/] ({sourceEnvironment.EnvironmentUrl}) - Type: {sourceEnvironment.Type})");
                config.SetEnvironment(settings.Environment, sourceEnvironment.Type == "Production");
            }

            // Validate solution name
            if (string.IsNullOrEmpty(settings.SolutionName))
            {
                AnsiConsole.MarkupLine(
                    "[red]Solution name is required. Please provide a solution name using -s <solutionName> or --solution <solutionName>.[/]");
                return 1;
            }

            // Save project configuration
            config.SolutionName = settings.SolutionName;
            config.UseManagedSolution = settings.Managed;
            config.Save();
            AnsiConsole.MarkupLine($"Project configuration saved to {ProjectConfig.ConfigFileName}.");
        }

        // Clone solution from Dataverse if it doesn't exist locally
        var srcSolutionFolder = Path.Combine(rootFolder, "src", settings.SolutionName);
        var cdsprojPath = Path.Combine(srcSolutionFolder, $"{settings.SolutionName}.cdsproj");
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
            AnsiConsole.MarkupLine($"[yellow]Found '{settings.SolutionName}.cdsproj'. Solution already cloned locally.[/]");
        }

        AnsiConsole.MarkupLine("[green]Initialization complete! You can now use 'sync' to keep your solution up to date.[/]");

        return 0;
    }
}
