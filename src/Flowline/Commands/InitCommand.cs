using System.ComponentModel;
using CliWrap;
using Flowline.Config;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class InitCommand : AsyncCommand<InitCommand.Settings>
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "[git-remote-url]")]
        [Description("Git repository URL")]
        public string GitRemoteUrl { get; set; } = null!; // = "https://github.com/AutomateValue/Dataverse01.git";

        [CommandOption("-e|--environment <URL>")]
        [Description("The environment to run the command against")]
        public string? Environment { get; set; }

        [CommandOption("-s|--solution")]
        [Description("The solution name to initialize")]
        [DefaultValue("Cr07982")]
        public string? SolutionName { get; set; } = "Cr07982";

        [CommandOption("--managed")]
        [Description("Use managed solution instead of unmanaged")]
        public bool Managed { get; set; } = false;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        await GitUtils.AssertGitInstalledAsync(cancellationToken);
        await PacUtils.AssertPacCliInstalledAsync(cancellationToken);

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
                                  .ExecuteAsync(cancellationToken);

            if (result.ExitCode != 0)
            {
                AnsiConsole.MarkupLine("[red]Failed to clone the repository. Please check the Git URL and your network connection.[/]");
                return 1;
            }
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]Git repository already initialized.[/]");

            (string? remoteName, string? remoteUrl) = await GitUtils.GetRemoteUrlAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(remoteUrl))
            {
                AnsiConsole.MarkupLineInterpolated($"  Remote URL: [link]{remoteUrl}[/] ({remoteName})");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]No remote configured for the current branch.[/]");
            }
        }

        // Load project configuration if exists
        var config = ProjectConfig.Load();
        if (config != null)
        {
            AnsiConsole.MarkupLine("[yellow]Project configuration already exists.[/]");
            if (string.IsNullOrEmpty(settings.Environment))
            {
                if (string.IsNullOrEmpty(config.ProductionEnvironment))
                {
                    AnsiConsole.MarkupLine("[red]No environment configured. Please provide an environment URL using -e <environment> or --environment <environment>.[/]");
                    return 1;
                }

                AnsiConsole.MarkupLine($"  Using configured production environment: [bold]{config.ProductionEnvironment}[/]");
            }
            else
            {
                var srcEnvironment = await PacUtils.GetEnvironmentInfoByUrlAsync(settings.Environment, cancellationToken);
                if (srcEnvironment == null)
                {
                    AnsiConsole.MarkupLine("[red]Invalid environment URL. Please provide a valid Dataverse environment URL.[/]");
                    return 1;
                }

                if (srcEnvironment.Type != "Production")
                {
                    AnsiConsole.MarkupLine("[red]Environment must be of type 'Production'.[/]");
                    return 1;
                }

                AnsiConsole.MarkupLine($"  Using environment: [bold]{srcEnvironment.DisplayName}[/] ({srcEnvironment.EnvironmentUrl}) - Type: {srcEnvironment.Type})");
                config.ProductionEnvironment = settings.Environment;
            }

            if (!string.IsNullOrEmpty(settings.SolutionName))
            {
                config.SolutionName = settings.SolutionName;
            }

            config.UseManagedSolution = settings.Managed;

            config.Save();
        }
        else
        {
            AnsiConsole.MarkupLine("No project configuration found. Creating a new one...");
            config = new ProjectConfig();

            // Environment exists?
            if (string.IsNullOrEmpty(settings.Environment))
            {
                AnsiConsole.MarkupLine("[red]Environment URL is required. Please provide a Dataverse environment URL using -e <environment> or --environment <environment>.[/]");
                return 1;
            }

            var srcEnvironment = await PacUtils.GetEnvironmentInfoByUrlAsync(settings.Environment, cancellationToken);
            if (srcEnvironment == null)
            {
                AnsiConsole.MarkupLine("[red]Invalid environment URL. Please provide a valid Dataverse environment URL.[/]");
                return 1;
            }

            if (srcEnvironment.Type != "Production")
            {
                AnsiConsole.MarkupLine("[red]Environment must be of type 'Production'.[/]");
                return 1;
            }

            AnsiConsole.MarkupLine($"  Using environment: [bold]{srcEnvironment.DisplayName}[/] ({srcEnvironment.EnvironmentUrl}) - Type: {srcEnvironment.Type})");
            config.ProductionEnvironment = settings.Environment;

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
        var srcSolutionFolder = Path.Combine(rootFolder, "solutions", config.SolutionName);
        var cdsprojPath = Path.Combine(srcSolutionFolder, $"{config.SolutionName}.cdsproj");
        if (!File.Exists(cdsprojPath))
        {
            AnsiConsole.MarkupLine($"No solution folder for '{config.SolutionName}' found. Cloning from Dataverse...");

            if (Directory.Exists(srcSolutionFolder))
            {
                AnsiConsole.MarkupLine("Removing existing solution folder...");
                Directory.Delete(srcSolutionFolder, true);
            }

            var result = await Cli.Wrap("pac")
                                  .WithArguments(args => args
                                                         .Add("solution")
                                                         .Add("clone")
                                                         .Add("--name").Add(config.SolutionName)
                                                         .Add("--async")
                                                         .Add("--environment").Add(config.ProductionEnvironment)
                                                         .Add("--packagetype").Add(config.UseManagedSolution ? "Both" : "Unmanaged")
                                                         .Add("--outputDirectory").Add($"{Path.Combine(rootFolder, "solutions")}"))
                                  .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]PAC: {s}[/]")))
                                  .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                                  .ExecuteAsync(cancellationToken);

            if (result.ExitCode != 0)
            {
                AnsiConsole.MarkupLine("[red]Failed to clone the solution. Please check the environment and solution name.[/]");
                return 1;
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]Found '{config.SolutionName}.cdsproj'. Solution already cloned locally.[/]");
        }

        AnsiConsole.MarkupLine("[green]Initialization complete! You can now use 'push' and 'export' (or 'sync') to keep your solution up to date.[/]");

        return 0;
    }
}
