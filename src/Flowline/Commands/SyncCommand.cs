using System.ComponentModel;
using System.Diagnostics;
using CliWrap;
using CliWrap.Buffered;
using Flowline.Config;
using Flowline.Core;
using Flowline.Utils;
using Spectre.Console;
using Spectre.Console.Cli;
using Command = CliWrap.Command;

namespace Flowline.Commands;

public class SyncCommand(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions) : FlowlineCommand<SyncCommand.Settings>(console, runtimeOptions)
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "[solution]")]
        [Description("Solution to sync")]
        public string? Solution { get; set; }

        [CommandOption("--dev <URL>")]
        [Description("Development environment URL")]
        public string? DevUrl { get; set; }

        [CommandOption("--managed")]
        [Description("Include managed artifacts")]
        public bool IncludeManaged { get; set; } = false;

        [CommandOption("--full")]
        [Description("Download all artifacts from Dataverse, including binaries (skips mapping)")]
        [DefaultValue(false)]
        public bool Full { get; set; } = false;

    }

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Dev URL is required
        var devEnv = await GetAndCheckEnvironmentInfoAsync(EnvironmentRole.Dev, settings.DevUrl, settings, cancellationToken);
        if (devEnv == null) return 1;

        // Solution name is required
        (ProjectSolution? projectSln, SolutionInfo? slnInfo) = await GetAndCheckSolutionAsync(settings.Solution, devEnv.EnvironmentUrl!, settings.IncludeManaged, settings, cancellationToken);
        if (projectSln == null || slnInfo == null) return 1;
        if (slnInfo.IsManaged)
        {
            Console.Error("Managed solutions are not supported for sync");
            return 1;
        }

        // Validate that we have an initialized project
        var slnFolder = Path.Combine(RootFolder, "solutions", projectSln.Name);
        var cdsprojPath = Path.Combine(slnFolder, $"{projectSln.Name}.cdsproj");
        if (!File.Exists(cdsprojPath))
        {
            Console.Error($"No solution found at '{cdsprojPath}' — run 'clone' first");
            return 1;
        }

        // Sync solution from Dataverse
        var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);
        var sw = Stopwatch.StartNew();
        CommandResult result = await Console.Status().FlowlineSpinner().StartAsync(
            $"Syncing solution [bold]{projectSln.Name}[/]...",
            ctx => Cli.Wrap(cmdName)
                    .WithArguments(args =>
                    {
                        args.AddIfNotNull(prefixArgs)
                            .Add("solution")
                            .Add("sync")
                            .Add("--solution-folder").Add(slnFolder)
                            .Add("--environment").Add(devEnv.EnvironmentUrl!)
                            .Add("--packagetype").Add(projectSln.IncludeManaged ? "Both" : "Unmanaged")
                            .Add("--async");
                        if (!settings.Full)
                            args.Add("--map").Add(Path.Combine(slnFolder, MappingPacFileName));
                    })
                    .WithValidation(CommandResultValidation.None)
                    .WithToolExecutionLog(settings.Verbose, ctx)
                    .ExecuteAsync(cancellationToken)
                    .Task);
        sw.Stop();

        if (!result.IsSuccess)
        {
            Console.Error("Sync failed — check the environment and your PAC login");
            return 1;
        }

        Console.Success($"Solution synced from Dataverse in {FormatDuration(sw.Elapsed)}");

        // Build the solution in dotnet to validate it (Debug = unmanaged, Release = managed!)
        if (await DotNetUtils.BuildSolutionAsync(slnFolder, DotnetBuild.Debug, settings.Verbose, cancellationToken) != 0)
        {
            return 1;
        }

        Console.Success("[bold]:rocket: Synced! Run 'git commit' to save a checkpoint.[/]");

        return 0;
    }

    static async Task GitCommitChanges(IAnsiConsole console, CancellationToken cancellationToken)
    {
        // Add all files to the git staging area
        await Cli.Wrap("git")
                 .WithArguments("add -A")
                 .WithStandardOutputPipe(PipeTarget.ToDelegate(s => console.MarkupLineInterpolated($"[dim]GIT: {s}[/]")))
                 .WithStandardErrorPipe(PipeTarget.ToDelegate(System.Console.Error.WriteLine))
                 .WithToolExecutionLog()
                 .ExecuteAsync(cancellationToken);

        // Check if there are changes to commit
        var statusResult = await Cli.Wrap("git")
                                    .WithArguments("status --porcelain")
                                    .WithToolExecutionLog()
                                    .ExecuteBufferedAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(statusResult.StandardOutput))
        {
            console.Skip("No changes — skipping commit");
            return;
        }

        // Commit the changes
        await Cli.Wrap("git")
                 .WithArguments(args => args
                                        .Add("commit")
                                        .Add("-m").Add("flowline: sync solution"))
                 .WithStandardOutputPipe(PipeTarget.ToDelegate(s => console.MarkupLineInterpolated($"[dim]GIT: {s}[/]")))
                 .WithStandardErrorPipe(PipeTarget.ToDelegate(System.Console.Error.WriteLine))
                 .WithToolExecutionLog()
                 .ExecuteAsync(cancellationToken);
    }
}
