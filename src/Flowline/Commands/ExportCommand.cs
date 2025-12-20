using System.ComponentModel;
using CliWrap;
using CliWrap.Buffered;
using Flowline.Config;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class ExportCommandSettings : FlowlineSettings
{
    [CommandOption("-e|--environment <URL>")]
    [Description("The environment to run the command against")]
    public string? Environment { get; set; }

    [CommandOption("-s|--solution")]
    [Description("The solution name to sync")]
    public string? SolutionName { get; set; }

    [CommandOption("-m|--message")]
    [Description("Commit message")]
    public string? CommitMessage { get; set; }

    [CommandOption("--no-auto-commit")]
    [Description("Do not automatically commit changes to the local repository and push them to the remote repository")]
    public bool NoAutoCommit { get; set; } = false;

    [CommandOption("--managed")]
    [Description("Use managed solution instead of unmanaged")]
    public bool Managed { get; set; } = false;
}

public class ExportCommand : AsyncCommand<ExportCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ExportCommandSettings settings)
    {
        await PacUtils.AssertPacCliInstalledAsync();

        // Load project configuration if needed
        var config = ProjectConfig.Load();

        // Use configuration values if not specified in command arguments
        var environment = settings.Environment ?? config?.DevelopmentEnvironment;
        var solutionName = settings.SolutionName ?? config?.SolutionName;
        var useManagedSolution = settings.Managed || config.UseManagedSolution;

        // Validate that we have an environment
        if (string.IsNullOrEmpty(environment))
        {
            AnsiConsole.MarkupLine("[red]No environment specified. Please provide an environment or run 'init' first.[/]");
            return 1;
        }

        var commitMessage = settings.CommitMessage ?? $"Commit changes to solution '{solutionName}' in environment '{environment}'";
        var rootFolder = Directory.GetCurrentDirectory();
        var srcSolutionFolder = Path.Combine(rootFolder, "src", "solutions", solutionName);
        var cdsprojPath = Path.Combine(srcSolutionFolder, $"{solutionName}.cdsproj");

        // Check if we're in an initialized environment
        if (!Directory.Exists(Path.Combine(rootFolder, ".git")))
        {
            AnsiConsole.MarkupLine("[red]This directory is not a git repository. Please run 'init' first.[/]");
            return 1;
        }

        if (!File.Exists(cdsprojPath))
        {
            AnsiConsole.MarkupLine($"[red]Solution '{solutionName}' not found. Please run 'init' first.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"Validating [bold]'{environment}'[/]...");

        var environments = await PacUtils.GetEnvironmentsAsync();
        var sourceEnv = environments.FirstOrDefault(e => e.EnvironmentUrl?.Contains(environment) == true);

        if (sourceEnv == null)
        {
            AnsiConsole.MarkupLine("[red]Source Environment not found.[/]");
            return 1;
        }

        if (sourceEnv.Type == "Production")
        {
            AnsiConsole.MarkupLine($"[red]Source environment type must NOT be 'Production' to be synced. Aborting.[/]");
            return 1;
        }

        // Perform sync
        AnsiConsole.MarkupLine($"Syncing solution '{solutionName}'...");

        var result = await Cli.Wrap("pac")
                              .WithArguments(args => args
                                    .Add("solution")
                                    .Add("sync")
                                    .Add("--solution-folder").Add(srcSolutionFolder)
                                    .Add("--environment").Add(environment)
                                    .Add("--packagetype").Add(useManagedSolution ? "Both" : "Unmanaged"))
                              .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]PAC: {s}[/]")))
                              .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                              .ExecuteAsync();

        if (result.ExitCode != 0)
        {
            AnsiConsole.MarkupLine("[red]Failed to sync the solution. Please check the environment and solution name.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"Building Solution '{solutionName}'...");

        await Cli.Wrap("dotnet")
                 .WithArguments(args => args
                      .Add("build")
                      .Add(srcSolutionFolder))
                      //.Add("--configuration").Add("Release")) // Release for Managed solution
                      //.Add("--output").Add(Path.Combine(rootFolder, "artifacts")))
                 .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]DOTNET: {s}[/]")))
                 .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                 .ExecuteAsync();

        if (settings.NoAutoCommit)
        {
            AnsiConsole.MarkupLine("[yellow]Skipping auto-commit and push. Use 'git add', 'git commit', and 'git push' manually.[/]");
            return 0;
        }

        await GitUtils.AssertGitInstalledAsync();

        // Add all files to the git staging area
        await Cli.Wrap("git")
                 .WithArguments("add -A")
                 .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]GIT: {s}[/]")))
                 .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                 .ExecuteAsync();

        // Check if there are changes to commit
        var statusResult = await Cli.Wrap("git")
                             .WithArguments("status --porcelain")
                             .ExecuteBufferedAsync();

        if (string.IsNullOrWhiteSpace(statusResult.StandardOutput))
        {
            AnsiConsole.MarkupLine("[yellow]No changes detected. Skipping commit and push.[/]");
            return 0;
        }

        // Commit the changes
        AnsiConsole.MarkupLine("Committing changes to local repository...");
        await Cli.Wrap("git")
                 .WithArguments(args => args
                      .Add("commit")
                      .Add("-m").Add(commitMessage))
                 .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]GIT: {s}[/]")))
                 .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                 .ExecuteAsync();

        // Push the changes
        AnsiConsole.MarkupLine("Pushing changes to remote repository...");
        await Cli.Wrap("git")
                 .WithArguments("push")
                 .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]GIT: {s}[/]")))
                 .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                 .ExecuteAsync();

        // Save or update the project configuration with any changes
        if (settings.Environment != null || settings.SolutionName != config.SolutionName || settings.Managed != config.UseManagedSolution)
        {
            // If the environment has changed, update it in config
            if (settings.Environment != null)
            {
                // Determine if this is production or development
                bool isProd = sourceEnv?.Type == "Production";
                if (isProd)
                {
                    config.ProductionEnvironment = environment;
                }
                else
                {
                    config.DevelopmentEnvironment = environment;
                }
            }

            config.SolutionName = solutionName;
            config.UseManagedSolution = useManagedSolution;
            config.Save();
            AnsiConsole.MarkupLine("[dim]Project configuration updated.[/]");
        }

        AnsiConsole.MarkupLine("[green]All done![/]");

        return 0;
    }
}
