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
        [Description("Destination environment or role such as `prod`, `staging`, or an explicit URL")]
        public string Target { get; set; } = null!;

        [CommandOption("--solution <name>")]
        [Description("Solution to package and deploy")]
        public string? Solution { get; set; }

        [CommandOption("--managed")]
        [Description("Deploy the managed package instead of unmanaged")]
        public bool Managed { get; set; } = false;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        await PacUtils.AssertPacCliInstalledAsync(cancellationToken);
        await GitUtils.AssertGitInstalledAsync(cancellationToken);

        var rootFolder = Directory.GetCurrentDirectory();
        await GitUtils.AssertGitRepoAsync(rootFolder, cancellationToken);
        await GitUtils.AssertRepoCleanAsync(cancellationToken);

        // Load or create the project configuration
        var config = ProjectConfig.Load();
        if (config == null)
        {
            AnsiConsole.MarkupLine("[yellow]No project configuration found. Creating...[/]");
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
            AnsiConsole.MarkupLine($"[red]Could not resolve target environment URL for '{settings.Target}'. Please provide an explicit URL or check your .flowline config.[/]");
            return 1;
        }

        // Resolve solution
        var sln = config.GetOrUpdateSolution(settings.Solution, settings.Managed, settings);
        if (sln == null)
        {
            AnsiConsole.MarkupLine("[red]Solution name is required. Please provide a solution name using --solution <name>.[/]");
            return 1;
        }

        // Validate target environment
        AnsiConsole.MarkupLine($"Validating [bold]'{targetUrl}'[/]...");
        var targetEnv = await PacUtils.GetEnvironmentInfoByUrlAsync(targetUrl, cancellationToken);
        if (targetEnv == null)
        {
            AnsiConsole.MarkupLine($"[red]Invalid target environment. Please provide a valid Dataverse environment URL.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"  Target environment: [bold]{targetEnv.DisplayName}[/] ({targetEnv.EnvironmentUrl}) - Type: {targetEnv.Type}");

        // Ask for confirmation if target is Production
        if (targetEnv.Type == "Production")
        {
            if (!ConsoleHelper.Confirm($"[yellow]Are you sure you want to deploy to PRODUCTION ([bold]{targetEnv.DisplayName}[/])?[/]", false, settings))
            {
                AnsiConsole.MarkupLine("[green]Deployment cancelled.[/]");
                return 0;
            }
        }

        var slnFolder = Path.Combine(rootFolder, "solutions", sln.Name);
        var packageFolder = Path.Combine(slnFolder, "SolutionPackage");
        var cdsprojPath = Path.Combine(packageFolder, "SolutionPackage.cdsproj");
        if (!File.Exists(cdsprojPath))
        {
            AnsiConsole.MarkupLine($"[red]Solution project '{sln.Name}' not found in '{cdsprojPath}'. Please run 'clone' first.[/]");
            return 1;
        }

        // Standard Dataverse solution build produces zip in bin/Debug for unmanaged or bin/Release for managed.
        // We assume Debug for simplicity, or we should check for built artifacts.
        // SyncCommand uses dotnet build <packageFolder> which defaults to Debug.
        var buildType = "Debug";
        var packagePath = Path.Combine(packageFolder, "bin", buildType, $"{sln.Name}{(sln.IncludeManaged ? "_managed" : "")}.zip");

        if (!File.Exists(packagePath))
        {
            AnsiConsole.MarkupLine($"[yellow]Solution package '{packagePath}' not found. Building...[/]");
            var buildResult = await Cli.Wrap("dotnet")
                                     .WithArguments(args => args
                                                          .Add("build")
                                                          .Add(packageFolder))
                                     .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]DOTNET: {s}[/]")))
                                     .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                                     .ExecuteAsync(cancellationToken);

            if (buildResult.ExitCode != 0)
            {
                AnsiConsole.MarkupLine("[red]Failed to build the solution project.[/]");
                return 1;
            }

            if (!File.Exists(packagePath))
            {
                AnsiConsole.MarkupLine($"[red]Built successfully, but could not find solution package at '{packagePath}'.[/]");
                return 1;
            }
        }

        AnsiConsole.MarkupLine($"Deploying [bold]{sln.Name}[/] {(sln.IncludeManaged ? "(managed) " : "")}to [bold]{targetEnv.DisplayName}[/]...");

        var importResult = await Cli.Wrap("pac")
                                    .WithArguments(args => args
                                                         .Add("solution")
                                                         .Add("import")
                                                         .Add("--path").Add(packagePath)
                                                         .Add("--environment").Add(targetEnv.EnvironmentUrl!)
                                                         .Add("--async"))
                                    .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]PAC: {s}[/]")))
                                    .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                                    .ExecuteAsync(cancellationToken);

        if (importResult.ExitCode != 0)
        {
            AnsiConsole.MarkupLine("[red]Failed to import the solution.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine("[green]All done![/]");

        return 0;
    }
}
