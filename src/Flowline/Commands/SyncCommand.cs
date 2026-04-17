using System.ComponentModel;
using CliWrap;
using CliWrap.Buffered;
using Flowline.Config;
using Flowline.Utils;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class SyncCommand : FlowlineCommand<SyncCommand.Settings>
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "[solution]")]
        [Description("optional solution override when multiple solutions exist")]
        public string? Solution { get; set; }

        [CommandOption("--dev <URL>")]
        [Description("Override the configured development environment")]
        public string? DevUrl { get; set; }

        [CommandOption("--managed")]
        [Description("Also sync managed artifacts in addition to unmanaged")]
        public bool IncludeManaged { get; set; } = false;

    }

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Dev URL is required
        var devEnv = await ResolveAndValidateDevUrlAsync(settings.DevUrl, settings, cancellationToken);
        if (devEnv == null) return 1;

        // Solution name is required
        var sln = await ResolveAndValidateSolutionAsync(settings.Solution, devEnv.EnvironmentUrl!, settings.IncludeManaged, settings, cancellationToken);
        if (sln == null) return 1;

        // Validate that we have an initialized project
        var slnFolder = Path.Combine(RootFolder, "solutions", sln.Name);
        var packageFolder = Path.Combine(slnFolder, "SolutionPackage");
        var cdsprojPath = Path.Combine(packageFolder, "SolutionPackage.cdsproj");
        if (!File.Exists(cdsprojPath))
        {
            AnsiConsole.MarkupLine($"[red]No solution found at '{cdsprojPath}' — run 'clone' first.[/]");
            return 1;
        }

        // Perform sync
        AnsiConsole.MarkupLine($"Syncing [bold]{sln.Name}[/]...");

        var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);
        var pacSolutionSyncCmd = Cli.Wrap(cmdName)
            .WithArguments(args => args
                .AddIfNotNull(prefixArgs)
                .Add("solution")
                .Add("sync")
                .Add("--solution-folder").Add(packageFolder)
                .Add("--environment").Add(devEnv.EnvironmentUrl!)
                .Add("--packagetype").Add(sln.IncludeManaged ? "Both" : "Unmanaged")
                .Add("--async"))
            .WithToolExecutionLog();

        var result = await pacSolutionSyncCmd
                              .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]PAC: {s}[/]")))
                              .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                              .ExecuteAsync(cancellationToken);

        if (result.ExitCode != 0)
        {
            AnsiConsole.MarkupLine("[red]Sync failed — check the environment and your PAC login.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"Building [bold]{sln.Name}[/]...");

        await Cli.Wrap("dotnet")
                 .WithArguments(args => args
                      .Add("build")
                      .Add(packageFolder))
                      //.Add("--configuration").Add("Release")) // Release for Managed solution
                      //.Add("--output").Add(Path.Combine(rootFolder, "artifacts")))
                 .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]DOTNET: {s}[/]")))
                 .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                 .WithToolExecutionLog()
                 .ExecuteAsync(cancellationToken);

        AnsiConsole.MarkupLine("[bold green]:white_check_mark: Synced! Run 'git commit' to save a checkpoint.[/]");

        return 0;
    }

    static async Task GitCommitChanges(CancellationToken cancellationToken)
    {
        // Add all files to the git staging area
        await Cli.Wrap("git")
                 .WithArguments("add -A")
                 .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]GIT: {s}[/]")))
                 .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                 .WithToolExecutionLog()
                 .ExecuteAsync(cancellationToken);

        // Check if there are changes to commit
        var statusResult = await Cli.Wrap("git")
                                    .WithArguments("status --porcelain")
                                    .WithToolExecutionLog()
                                    .ExecuteBufferedAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(statusResult.StandardOutput))
        {
            AnsiConsole.MarkupLine("[yellow]No changes detected. Skipping commit.[/]");
            return;
        }

        // Commit the changes
        AnsiConsole.MarkupLine("Committing changes to local repository...");
        await Cli.Wrap("git")
                 .WithArguments(args => args
                                        .Add("commit")
                                        .Add("-m").Add("flowline: sync solution"))
                 .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]GIT: {s}[/]")))
                 .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                 .WithToolExecutionLog()
                 .ExecuteAsync(cancellationToken);
    }
}
