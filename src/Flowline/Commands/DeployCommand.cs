using System.ComponentModel;
using CliWrap;
using Flowline.Core;
using Flowline.Utils;
using Flowline.Validation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class DeployCommand(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions) : FlowlineCommand<DeployCommand.Settings>(console, runtimeOptions)
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "<target>")]
        [Description("Target environment: prod, test, or a URL")]
        public string Target { get; set; } = null!;

        [CommandOption("--solution <name>")]
        [Description("Solution to deploy")]
        public string? Solution { get; set; }

        [CommandOption("--managed")]
        [Description("Deploy the managed package")]
        [DefaultValue(false)]
        public bool Managed { get; set; } = false;
    }

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        await GitUtils.AssertRepoCleanAsync(settings.Verbose, cancellationToken);

        // Determine target URL
        var targetUrl = settings.Target.ToLowerInvariant() switch
        {
            "prod" => Config!.ProdUrl,
            "test" => Config!.TestUrl,
            _ => settings.Target
        };

        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            Console.MarkupLine($"[red]Can't resolve '{settings.Target}' — provide an explicit URL or check your .flowline config.[/]");
            return 1;
        }

        // Resolve solution
        var sln = Config!.GetOrUpdateSolution(settings.Solution, settings.Managed, settings);
        if (sln == null)
        {
            Console.MarkupLine("[red]Solution name is required — use --solution <name>.[/]");
            return 1;
        }

        // Validate target environment
        var targetEnv = await Console.Status().FlowlineSpinner().StartAsync(
            $"Checking [bold]{targetUrl}[/]...",
            _ => FlowlineValidator.Default.GetEnvironmentInfoByUrlAsync(targetUrl, settings, cancellationToken));
        if (targetEnv == null)
        {
            Console.MarkupLine("[red]Target environment not found. Check the URL or your PAC login.[/]");
            return 1;
        }

        Console.MarkupLine($"[green]Target: [bold]{targetEnv.DisplayName}[/] ({targetEnv.EnvironmentUrl})[/]");

        // Ask for confirmation if target is Production
        if (targetEnv.Type == "Production")
        {
            if (!ConsoleHelper.Confirm($"[yellow]Are you sure you want to deploy to PRODUCTION ([bold]{targetEnv.DisplayName}[/])?[/]", false, settings))
            {
                Console.MarkupLine("[dim]Deploy cancelled[/]");
                return 0;
            }
        }

        var slnFolder = Path.Combine(RootFolder, "solutions", sln.Name);
        var cdsprojPath = Path.Combine(PackageFolder(slnFolder), $"{PackageName}.cdsproj");
        if (!File.Exists(cdsprojPath))
        {
            Console.MarkupLine($"[red]No solution found at '{cdsprojPath}' — run 'clone' first.[/]");
            return 1;
        }

        // Block if local changes haven't been synced — deploy packs from src/, not dist/
        var drift = DriftChecker.Check(slnFolder, PackageFolder(slnFolder), cancellationToken: cancellationToken)
            .Where(w => w.Category is DriftCategory.OnlyLocal or DriftCategory.PluginSizeMismatch)
            .ToList();
        if (drift.Count > 0)
        {
            Console.Error("Local changes not in Dataverse — deploy would revert them. Run 'sync' first, or use --force to skip.");
            foreach (var w in drift)
                Console.Warning(w.Category == DriftCategory.OnlyLocal
                    ? $"Only local: {w.RelativePath}"
                    : $"Plugin size mismatch: {w.RelativePath}");
            if (!settings.Force) return 1;
        }

        var artifactsFolder = Path.Combine(slnFolder, "artifacts");
        Directory.CreateDirectory(artifactsFolder);

        var packageType = settings.Managed ? "Managed" : "Unmanaged";
        var suffix = settings.Managed ? "_managed" : "_unmanaged";
        var packagePath = Path.Combine(artifactsFolder, $"{sln.Name}{suffix}.zip");

        var (cmdNamePack, prefixArgsPack, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);
        var packResult = await Console.Status().FlowlineSpinner().StartAsync(
            $"Packing [bold]{sln.Name}[/]...",
            _ => Cli.Wrap(cmdNamePack)
                    .WithArguments(args => args
                        .AddIfNotNull(prefixArgsPack)
                        .Add("solution")
                        .Add("pack")
                        .Add("--folder").Add(Path.Combine(PackageFolder(slnFolder), "src"))
                        .Add("--zipFile").Add(packagePath)
                        .Add("--packageType").Add(packageType))
                    .WithValidation(CommandResultValidation.None)
                    .WithToolExecutionLog(settings.Verbose)
                    .ExecuteAsync(cancellationToken)
                    .Task);

        if (packResult.ExitCode != 0)
        {
            Console.MarkupLine("[red]Pack failed — check your solution source.[/]");
            return 1;
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
            .WithValidation(CommandResultValidation.None)
            .WithToolExecutionLog();

        var importResult = await Console.Status().FlowlineSpinner().StartAsync(
            $"Deploying [bold]{sln.Name}[/] to [bold]{targetEnv.DisplayName}[/]...",
            _ => pacSolutionImportCmd
                    .WithStandardOutputPipe(PipeTarget.ToDelegate(s => Console.MarkupLineInterpolated($"[dim]PAC: {s}[/]")))
                    .WithStandardErrorPipe(PipeTarget.ToDelegate(System.Console.Error.WriteLine))
                    .ExecuteAsync(cancellationToken)
                    .Task);

        if (importResult.ExitCode != 0)
        {
            Console.MarkupLine("[red]Deploy failed — check the environment and your PAC login.[/]");
            return 1;
        }

        Console.Done("Deployed! Your solution is live.");

        return 0;
    }
}
