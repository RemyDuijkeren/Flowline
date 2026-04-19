using Flowline.Config;
using Flowline.Utils;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public enum EnvironmentRole { Prod, Staging, Dev }

public abstract class FlowlineCommand<TSettings> : AsyncCommand<TSettings> where TSettings : FlowlineSettings
{
    protected const string AllSolutionsFolderName = "solutions";
    protected const string SolutionPackageName = "SolutionPackage";
    protected const string WebResourcesName = "WebResources";
    protected const string ExtensionsName = "Extensions";

    protected string RootFolder { get; private set; } = Directory.GetCurrentDirectory();
    protected ProjectConfig? Config { get; private set; }

    protected override async Task<int> ExecuteAsync(CommandContext context, TSettings settings, CancellationToken cancellationToken)
    {
        await CheckSetupAsync(settings, cancellationToken);

        Config = ProjectConfig.Load(RootFolder) ?? new ProjectConfig();

        return await ExecuteFlowlineAsync(context, settings, cancellationToken);
    }

    protected abstract Task<int> ExecuteFlowlineAsync(CommandContext context, TSettings settings, CancellationToken cancellationToken);

    protected virtual async Task CheckSetupAsync(TSettings settings, CancellationToken cancellationToken)
    {
        await AnsiConsole.Status().FlowlineSpinner().StartAsync("Checking your setup...", async ctx =>
        {
            await DotNetUtils.AssertDotNetInstalledAsync(settings.Verbose, cancellationToken);
            await PacUtils.AssertPacCliInstalledAsync(settings.Verbose, cancellationToken);
            await GitUtils.AssertGitInstalledAsync(settings.Verbose, cancellationToken);
            await GitUtils.AssertGitRepoAsync(RootFolder, settings.Verbose, cancellationToken);
        });

        AnsiConsole.MarkupLine("[green]All good, let's go![/]");
    }

    protected async Task<EnvironmentInfo?> GetAndCheckEnvironmentInfoAsync(EnvironmentRole role, string? inputUrl, TSettings settings, CancellationToken cancellationToken)
    {
        var label = role switch
        {
            EnvironmentRole.Prod    => "Prod",
            EnvironmentRole.Staging => "Staging",
            EnvironmentRole.Dev     => "Dev",
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };
        var flag = role switch
        {
            EnvironmentRole.Prod    => "--prod",
            EnvironmentRole.Staging => "--staging",
            EnvironmentRole.Dev     => "--dev",
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };

        var url = role switch
        {
            EnvironmentRole.Prod    => Config!.GetOrUpdateProdUrl(inputUrl, settings),
            EnvironmentRole.Staging => Config!.GetOrUpdateStagingUrl(inputUrl, settings),
            EnvironmentRole.Dev     => Config!.GetOrUpdateDevUrl(inputUrl, settings),
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };

        if (string.IsNullOrEmpty(url))
        {
            AnsiConsole.MarkupLine($"[red]{label} URL is required — use {flag} <URL>.[/]");
            return null;
        }

        EnvironmentInfo? env = await AnsiConsole.Status().FlowlineSpinner().StartAsync(
            $"Checking {label.ToLower()} [bold]{url}[/]...",
            ctx => PacUtils.GetEnvironmentInfoByUrlAsync(url, settings.Verbose, cancellationToken));

        if (env == null)
        {
            AnsiConsole.MarkupLine($"[red]{label} environment not found — check the URL or your PAC login.[/]");
            return null;
        }

        if (role == EnvironmentRole.Prod && env.Type != "Production")
        {
            AnsiConsole.MarkupLine("[red]That environment isn't Production type.[/]");
            return null;
        }

        if (role != EnvironmentRole.Prod && env.Type == "Production")
        {
            AnsiConsole.MarkupLine("[red]That's a Production environment — use a sandbox or dev instead.[/]");
            return null;
        }

        AnsiConsole.MarkupLine($"[green]{label}: [bold]{env.DisplayName}[/] ({env.EnvironmentUrl})[/]");
        return env;
    }

    protected async Task<ProjectSolution?> GetAndCheckSolutionAsync(string? inputName, string environmentUrl, bool includeManaged, TSettings settings, CancellationToken cancellationToken)
    {
        var sln = Config!.GetOrUpdateSolution(inputName, includeManaged, settings);
        if (sln == null)
        {
            AnsiConsole.MarkupLine("[red]Solution name is required — pass it as an argument or use --solution <name>.[/]");
            return null;
        }

        List<SolutionInfo> solutions = await AnsiConsole.Status().FlowlineSpinner().StartAsync(
            $"Looking up [bold]{sln.Name}[/]...",
            ctx => PacUtils.GetSolutionsAsync(environmentUrl, settings.Verbose, cancellationToken));

        var remoteSolution = solutions.FirstOrDefault(s => s.SolutionUniqueName?.Equals(sln.Name, StringComparison.OrdinalIgnoreCase) == true);
        if (remoteSolution == null)
        {
            AnsiConsole.MarkupLine($"[red]'{sln.Name}' not found in that environment.[/]");
            return null;
        }

        if (remoteSolution.IsManaged && !sln.IncludeManaged)
        {
            AnsiConsole.MarkupLine($"[red][bold]{sln.Name}[/] is managed — add --managed to include managed artifacts.[/]");
            return null;
        }

        AnsiConsole.MarkupLine($"[green]Solution: [bold]{sln.Name}[/] (managed: {remoteSolution.IsManaged})[/]");

        return sln;
    }
}
