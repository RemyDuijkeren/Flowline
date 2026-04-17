using Flowline.Config;
using Flowline.Utils;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public abstract class FlowlineCommand<TSettings> : AsyncCommand<TSettings> where TSettings : FlowlineSettings
{
    protected const string AllSolutionsFolderName = "solutions";
    protected const string SolutionPackageName = "SolutionPackage";
    protected const string WebResourcesName = "WebResources";
    protected const string ExtensionsName = "Extensions";

    protected string RootFolder { get; private set; } = Directory.GetCurrentDirectory();
    protected ProjectConfig? Config { get; private set; }

    public override async Task<int> ExecuteAsync(CommandContext context, TSettings settings, CancellationToken cancellationToken)
    {
        await ValidateEnvironmentAsync(settings, cancellationToken);

        Config = ProjectConfig.Load(RootFolder) ?? new ProjectConfig();

        return await ExecuteFlowlineAsync(context, settings, cancellationToken);
    }

    protected abstract Task<int> ExecuteFlowlineAsync(CommandContext context, TSettings settings, CancellationToken cancellationToken);

    protected virtual async Task ValidateEnvironmentAsync(TSettings settings, CancellationToken cancellationToken)
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

    protected async Task<EnvironmentInfo?> ResolveAndValidateProdUrlAsync(string? inputUrl, TSettings settings, CancellationToken cancellationToken)
    {
        var prodUrl = Config!.GetOrUpdateProdUrl(inputUrl, settings);
        if (prodUrl == null)
        {
            AnsiConsole.MarkupLine("[red]Prod URL is required — use --prod <URL>.[/]");
            return null;
        }

        EnvironmentInfo? srcEnvironment = await AnsiConsole.Status().FlowlineSpinner().StartAsync(
            $"Checking prod [bold]'{prodUrl}'[/]...",
            ctx => PacUtils.GetEnvironmentInfoByUrlAsync(prodUrl, settings.Verbose, cancellationToken));

        if (srcEnvironment == null)
        {
            AnsiConsole.MarkupLine("[red]Prod environment not found. Check the URL or your PAC login.[/]");
            return null;
        }

        if (srcEnvironment.Type != "Production")
        {
            AnsiConsole.MarkupLine("[red]That environment isn't Production type.[/]");
            return null;
        }

        AnsiConsole.MarkupLine($"[green]Prod: [bold]{srcEnvironment.DisplayName}[/] ({srcEnvironment.EnvironmentUrl})[/]");
        return srcEnvironment;
    }

    protected async Task<EnvironmentInfo?> ResolveAndValidateDevUrlAsync(string? inputUrl, TSettings settings, CancellationToken cancellationToken)
    {
        var devUrl = Config!.GetOrUpdateDevUrl(inputUrl, settings);
        if (string.IsNullOrEmpty(devUrl))
        {
            AnsiConsole.MarkupLine("[red]Dev URL is required — use --dev <URL>.[/]");
            return null;
        }

        EnvironmentInfo? devEnv = await AnsiConsole.Status().FlowlineSpinner().StartAsync(
            $"Checking dev [bold]'{devUrl}'[/]...",
            ctx => PacUtils.GetEnvironmentInfoByUrlAsync(devUrl, settings.Verbose, cancellationToken));

        if (devEnv == null)
        {
            AnsiConsole.MarkupLine("[red]Dev environment not found. Check the URL or your PAC login.[/]");
            return null;
        }

        if (devEnv.Type == "Production")
        {
            AnsiConsole.MarkupLine("[red]That's a Production environment — use a dev or sandbox instead.[/]");
            return null;
        }

        AnsiConsole.MarkupLine($"[green]Dev: [bold]{devEnv.DisplayName}[/] ({devEnv.EnvironmentUrl})[/]");
        return devEnv;
    }

    protected async Task<ProjectSolution?> ResolveAndValidateSolutionAsync(string? inputName, string environmentUrl, bool includeManaged, TSettings settings, CancellationToken cancellationToken)
    {
        var sln = Config!.GetOrUpdateSolution(inputName, includeManaged, settings);
        if (sln == null)
        {
            AnsiConsole.MarkupLine("[red]Solution name is required — pass it as an argument or use --solution <name>.[/]");
            return null;
        }

        List<SolutionInfo> solutions = await AnsiConsole.Status().FlowlineSpinner().StartAsync(
            $"Looking up [bold]'{sln.Name}'[/]...",
            ctx => PacUtils.GetSolutionsAsync(environmentUrl, settings.Verbose, cancellationToken));

        var remoteSolution = solutions.FirstOrDefault(s => s.SolutionUniqueName?.Equals(sln.Name, StringComparison.OrdinalIgnoreCase) == true);
        if (remoteSolution == null)
        {
            AnsiConsole.MarkupLine($"[red]'{sln.Name}' not found in that environment.[/]");
            return null;
        }

        if (remoteSolution.IsManaged && !sln.IncludeManaged)
        {
            AnsiConsole.MarkupLine($"[red]'{sln.Name}' is managed — add --managed to include managed artifacts.[/]");
            return null;
        }

        AnsiConsole.MarkupLine($"[green]Solution: [bold]'{sln.Name}'[/] (managed: {remoteSolution.IsManaged})[/]");

        return sln;
    }
}
