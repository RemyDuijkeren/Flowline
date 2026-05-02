using System.ComponentModel;
using CliWrap;
using Flowline.Config;
using Flowline.Utils;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class DeployCommand : AsyncCommand<DeployCommand.Settings>
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "<target>")]
        [Description("Target environment: prod, staging, or a URL")]
        public string Target { get; set; } = null!;

        [CommandOption("--solution <name>")]
        [Description("Solution to deploy")]
        public string? Solution { get; set; }

        [CommandOption("--managed")]
        [Description("Deploy the managed package")]
        public bool Managed { get; set; } = false;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        await DotNetUtils.AssertDotNetInstalledAsync(settings.Verbose, cancellationToken);
        await PacUtils.AssertPacCliInstalledAsync(settings.Verbose, cancellationToken);
        await GitUtils.AssertGitInstalledAsync(settings.Verbose, cancellationToken);

        var rootFolder = Directory.GetCurrentDirectory();
        await GitUtils.AssertGitRepoAsync(rootFolder, settings.Verbose, cancellationToken);
        await GitUtils.AssertRepoCleanAsync(settings.Verbose, cancellationToken);

        // Load or create the project configuration
        var config = ProjectConfig.Load();
        if (config == null)
        {
            AnsiConsole.MarkupLine("[dim]No .flowline config found — starting fresh[/]");
            config = new ProjectConfig();
        }

        // Determine target URL
        var targetUrl = settings.Target.ToLowerInvariant() switch
        {
            "prod" => config.ProdUrl,
            "staging" => config.StagingUrl,
            _ => settings.Target
        };

        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            AnsiConsole.MarkupLine($"[red]Can't resolve '{settings.Target}' — provide an explicit URL or check your .flowline config.[/]");
            return 1;
        }

        // Resolve solution
        var sln = config.GetOrUpdateSolution(settings.Solution, settings.Managed, settings);
        if (sln == null)
        {
            AnsiConsole.MarkupLine("[red]Solution name is required — use --solution <name>.[/]");
            return 1;
        }

        // Validate target environment
        var targetEnv = await AnsiConsole.Status().FlowlineSpinner().StartAsync(
            $"Checking [bold]{targetUrl}[/]...",
            _ => PacUtils.GetEnvironmentInfoByUrlAsync(targetUrl, settings.Verbose, cancellationToken));
        if (targetEnv == null)
        {
            AnsiConsole.MarkupLine("[red]Target environment not found. Check the URL or your PAC login.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[green]Target: [bold]{targetEnv.DisplayName}[/] ({targetEnv.EnvironmentUrl})[/]");

        // Ask for confirmation if target is Production
        if (targetEnv.Type == "Production")
        {
            if (!ConsoleHelper.Confirm($"[yellow]Are you sure you want to deploy to PRODUCTION ([bold]{targetEnv.DisplayName}[/])?[/]", false, settings))
            {
            AnsiConsole.MarkupLine("[dim]Deploy cancelled[/]");
                return 0;
            }
        }

        var slnFolder = Path.Combine(rootFolder, "solutions", sln.Name);
        var packageFolder = Path.Combine(slnFolder, "SolutionPackage");
        var cdsprojPath = Path.Combine(packageFolder, "SolutionPackage.cdsproj");
        if (!File.Exists(cdsprojPath))
        {
            AnsiConsole.MarkupLine($"[red]No solution found at '{cdsprojPath}' — run 'clone' first.[/]");
            return 1;
        }

        // Standard Dataverse solution build produces zip in bin/Debug for unmanaged or bin/Release for managed.
        // We assume Debug for simplicity, or we should check for built artifacts.
        // SyncCommand uses dotnet build <packageFolder> which defaults to Debug.
        var buildType = "Debug";
        var packagePath = Path.Combine(packageFolder, "bin", buildType, $"{sln.Name}{(sln.IncludeManaged ? "_managed" : "")}.zip");

        if (!File.Exists(packagePath))
        {
            AnsiConsole.MarkupLine("[dim]No package found — building first[/]");
            var buildResult = await AnsiConsole.Status().FlowlineSpinner().StartAsync(
                $"Building [bold]{sln.Name}[/]...",
                _ => Cli.Wrap("dotnet")
                                     .WithArguments(args => args
                                                          .Add("build")
                                                          .Add(packageFolder))
                                     .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]DOTNET: {s}[/]")))
                                     .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                                     .WithToolExecutionLog()
                                     .ExecuteAsync(cancellationToken)
                                     .Task);

            if (buildResult.ExitCode != 0)
            {
                AnsiConsole.MarkupLine("[red]Build failed — check the output above. Use --verbose for details.[/]");
                return 1;
            }

            if (!File.Exists(packagePath))
            {
                AnsiConsole.MarkupLine($"[red]Build done but no package at '{packagePath}'.[/]");
                return 1;
            }
        }

        var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);
        var pacSolutionImportCmd = Cli.Wrap(cmdName)
            .WithArguments(args => args
                .AddIfNotNull(prefixArgs)
                .Add("solution")
                .Add("import")
                .Add("--path").Add(packagePath)
                .Add("--environment").Add(targetEnv.EnvironmentUrl!)
                .Add("--async"))
            .WithToolExecutionLog();

        var importResult = await AnsiConsole.Status().FlowlineSpinner().StartAsync(
            $"Deploying [bold]{sln.Name}[/] to [bold]{targetEnv.DisplayName}[/]...",
            _ => pacSolutionImportCmd
                                    .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]PAC: {s}[/]")))
                                    .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                                    .ExecuteAsync(cancellationToken)
                                    .Task);

        if (importResult.ExitCode != 0)
        {
            AnsiConsole.MarkupLine("[red]Deploy failed — check the environment and your PAC login.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine("[bold green]:rocket: Deployed! Your solution is live.[/]");

        return 0;
    }
}
