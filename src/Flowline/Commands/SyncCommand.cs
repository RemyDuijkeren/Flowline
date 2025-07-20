using System.ComponentModel;
using CliWrap;
using CliWrap.Buffered;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class SyncCommandSettings : BaseCommandSettings
{
    [CommandArgument(0, "<environment>")]
    [Description("The environment to run the command against")]
    public string Environment { get; set; }  = null!; //= "https://automatevalue-dev.crm4.dynamics.com/";

    [CommandOption("-s|--solution")]
    [Description("The solution name to sync")]
    [DefaultValue("Cr07982")]
    public string SolutionName { get; set; } = "Cr07982";

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

public class SyncCommand : AsyncCommand<SyncCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, SyncCommandSettings settings)
    {
        await PacUtils.AssertPacCliInstalledAsync();

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

        AnsiConsole.MarkupLine($"Validating [bold]'{settings.Environment}'[/]...");

        var environments = await PacUtils.GetEnvironmentsAsync();
        var sourceEnv = environments.FirstOrDefault(e => e.EnvironmentUrl?.Contains(settings.Environment) == true);

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
        AnsiConsole.MarkupLine($"Syncing solution '{settings.SolutionName}'...");

        var result = await Cli.Wrap("pac")
                              .WithArguments(args => args
                                    .Add("solution")
                                    .Add("sync")
                                    .Add("--solution-folder")
                                    .Add(srcSolutionFolder)
                                    .Add("--environment").Add(settings.Environment)
                                    .Add("--packagetype").Add(settings.Managed ? "Both" : "Unmanaged"))
                              .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]{s}[/]")))
                              .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                              .ExecuteAsync();

        if (result.ExitCode != 0)
        {
            AnsiConsole.MarkupLine("[red]Failed to sync the solution. Please check the environment and solution name.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"Building Solution '{settings.SolutionName}'...");

        await Cli.Wrap("dotnet")
                 .WithArguments(args => args
                      .Add("build")
                      .Add(srcSolutionFolder))
                      //.Add("--configuration").Add("Release")) // Release for Managed solution
                      //.Add("--output").Add(Path.Combine(rootFolder, "artifacts")))
                 .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]{s}[/]")))
                 .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                 .ExecuteAsync();

        if (settings.NoAutoCommit)
        {
            AnsiConsole.MarkupLine("[yellow]Skipping auto-commit and push. Use 'git add', 'git commit', and 'git push' manually.[/]");
            return 0;
        }

        await PacUtils.AssertGitInstalledAsync();

        // Add all files to the git staging area
        await Cli.Wrap("git")
                 .WithArguments("add -A")
                 .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]{s}[/]")))
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
                 .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]{s}[/]")))
                 .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                 .ExecuteAsync();

        // Push the changes
        AnsiConsole.MarkupLine("Pushing changes to remote repository...");
        await Cli.Wrap("git")
                 .WithArguments("push")
                 .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]{s}[/]")))
                 .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                 .ExecuteAsync();

        AnsiConsole.MarkupLine("[green]All done![/]");

        return 0;
    }
}
