using System.ComponentModel;
using CliWrap;
using CliWrap.Buffered;
using Flowline.Config;
using Flowline.Utils;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class SyncCommand : AsyncCommand<SyncCommand.Settings>
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

    public override async Task<int> ExecuteAsync(CommandContext context, SyncCommand.Settings settings, CancellationToken cancellationToken)
    {
        await DotNetUtils.AssertDotNetInstalledAsync(settings.Verbose, cancellationToken);
        await GitUtils.AssertGitInstalledAsync(settings.Verbose, cancellationToken);
        await PacUtils.AssertPacCliInstalledAsync(settings.Verbose, cancellationToken);

        var rootFolder = Directory.GetCurrentDirectory();
        await GitUtils.AssertGitRepoAsync(rootFolder, settings.Verbose, cancellationToken);

        // Load or create the project configuration
        var config = ProjectConfig.Load();
        if (config != null)
        {
            AnsiConsole.MarkupLine("[yellow]Project configuration already exists.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("No project configuration found. Creating...");
            config = new ProjectConfig();
        }

        // Solution name is required
        var sln = config.GetOrUpdateSolution(settings.Solution, settings.IncludeManaged, settings);
        if (sln == null)
        {
            AnsiConsole.MarkupLine("[red]Solution name is required. Please provide a solution name using 'sync <solutionName>'.[/]");
            return 1;
        }

        // Dev URL is required
        var devUrl = config.GetOrUpdateDevUrl(settings.DevUrl, settings);
        if (string.IsNullOrEmpty(devUrl))
        {
            AnsiConsole.MarkupLine("[red]Dev URL is required. Please provide a dev URL using 'sync <solutionName> --dev <URL>'.[/]");
            return 1;
        }


        // Validate Dev URL
        AnsiConsole.MarkupLine($"Validating [bold]'{devUrl}'[/]...");
        var devEnv = await PacUtils.GetEnvironmentInfoByUrlAsync(devUrl, settings.Verbose, cancellationToken);
        if (devEnv == null)
        {
            AnsiConsole.MarkupLine("[red]Invalid Dev environment. Please provide a valid Dataverse environment URL using --dev <environment-url>.[/]");
            return 1;
        }

        if (devEnv.Type == "Production")
        {
            AnsiConsole.MarkupLine("[red]Dev environment must not be of type 'Production'.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"  Using Dev environment: [bold]{devEnv.DisplayName}[/] ({devEnv.EnvironmentUrl}) - Type: {devEnv.Type})");


        // Validate that we have an initialized project
        var slnFolder = Path.Combine(rootFolder, "solutions", sln.Name);
        var packageFolder = Path.Combine(slnFolder, "SolutionPackage");
        var cdsprojPath = Path.Combine(packageFolder, "SolutionPackage.cdsproj");
        if (!File.Exists(cdsprojPath))
        {
            AnsiConsole.MarkupLine($"[red]Solution project '{sln.Name}' not found in '{cdsprojPath}'. Please run 'clone' first.[/]");
            return 1;
        }

        // Perform sync
        AnsiConsole.MarkupLine($"Syncing solution '{sln.Name}' from environment '{devEnv.DisplayName}' ({devEnv.EnvironmentUrl})...");

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
            AnsiConsole.MarkupLine("[red]Failed to sync the solution. Please check the environment and solution name.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"Building Solution '{sln.Name}'...");

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

        AnsiConsole.MarkupLine("[green]All done! Use 'git add' and 'git commit' to create a checkpoint.[/]");

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
