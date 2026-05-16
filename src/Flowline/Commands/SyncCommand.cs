using System.ComponentModel;
using System.Diagnostics;
using CliWrap;
using CliWrap.Buffered;
using Flowline.Config;
using Flowline.Core;
using Flowline.Utils;
using Spectre.Console;
using Spectre.Console.Cli;

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

        // Check for uncommitted changes
        var srcPath = Path.Combine(slnFolder, "src");
        var dirty = await GitUtils.GetUncommittedChangesInPathAsync(srcPath, workingDirectory: RootFolder, verbose: settings.Verbose, cancellationToken: cancellationToken);
        if (dirty.Count > 0)
        {
            if (!settings.Force)
            {
                Console.Error($"Uncommitted changes in '{projectSln.Name}/src/' — git stash or commit first, or re-run with --force.");
                foreach (var file in dirty)
                    Console.Skip($"  {Markup.Escape(file)}");
                return 1;
            }
            Console.Warning($"Uncommitted changes in '{projectSln.Name}/src/' — overwriting.");
            foreach (var file in dirty)
                Console.Skip($"  {Markup.Escape(file)}");
        }

        if (await DotNetUtils.EnsureMapFilePathAsync(cdsprojPath, projectSln.UseMapping, cancellationToken) != 0) return 1;

        // Sync solution from Dataverse
        var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);
        var sw = Stopwatch.StartNew();
        CommandResult result = await Console.Status().FlowlineSpinner().StartAsync(
            $"Syncing solution [bold]{projectSln.Name}[/]...",
            ctx => Cli.Wrap(cmdName)
                      .WithArguments(args =>
                          args.AddIfNotNull(prefixArgs)
                              .Add("solution")
                              .Add("sync")
                              .Add("--solution-folder").Add(slnFolder)
                              .Add("--environment").Add(devEnv.EnvironmentUrl!)
                              .Add("--packagetype").Add(projectSln.IncludeManaged ? "Both" : "Unmanaged")
                              .AddIf(projectSln.UseMapping, "--map", Path.Combine(slnFolder, MappingPacFileName))
                              .Add("--async"))
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

        try
        {
            var summary = await SolutionChangeSummary.ComputeAsync(Path.Combine(slnFolder, "src"), RootFolder, cancellationToken);
            summary.Write(Console, devEnv.DisplayName, settings.Verbose);
        }
        catch
        {
            Console.Skip("Change summary unavailable");
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
