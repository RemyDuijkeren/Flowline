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
        await AnsiConsole.Status().StartAsync("Validating environment...", async ctx =>
        {
            await DotNetUtils.AssertDotNetInstalledAsync(settings.Verbose, cancellationToken);
            await PacUtils.AssertPacCliInstalledAsync(settings.Verbose, cancellationToken);
            await GitUtils.AssertGitInstalledAsync(settings.Verbose, cancellationToken);
            await GitUtils.AssertGitRepoAsync(RootFolder, settings.Verbose, cancellationToken);
        });

        AnsiConsole.MarkupLine("[green]Valid Environment[/]");
    }

    protected async Task<EnvironmentInfo?> ResolveAndValidateProdUrlAsync(string? inputUrl, TSettings settings, CancellationToken cancellationToken)
    {
        var prodUrl = Config!.GetOrUpdateProdUrl(inputUrl, settings);
        if (prodUrl == null)
        {
            AnsiConsole.MarkupLine("[red]Production environment is required. Please provide a Dataverse environment URL using --prod <URL>.[/]");
            return null;
        }

        EnvironmentInfo? srcEnvironment = await AnsiConsole.Status().Spinner(spinner: Spinner.Known.Star2).StartAsync(
            $"Validating Production environment [bold]'{prodUrl}'[/]...",
            ctx => PacUtils.GetEnvironmentInfoByUrlAsync(prodUrl, settings.Verbose, cancellationToken));

        if (srcEnvironment == null)
        {
            AnsiConsole.MarkupLine("[red]Invalid Production environment. Please provide a valid Dataverse environment URL using --prod <URL>.[/]");
            return null;
        }

        if (srcEnvironment.Type != "Production")
        {
            AnsiConsole.MarkupLine("[red]Production environment must be of type 'Production'.[/]");
            return null;
        }

        AnsiConsole.MarkupLine($"[green]Valid Production environment: [bold]{srcEnvironment.DisplayName}[/] ({srcEnvironment.EnvironmentUrl}, Type: {srcEnvironment.Type})[/]");
        return srcEnvironment;
    }

    protected async Task<EnvironmentInfo?> ResolveAndValidateDevUrlAsync(string? inputUrl, TSettings settings, CancellationToken cancellationToken)
    {
        var devUrl = Config!.GetOrUpdateDevUrl(inputUrl, settings);
        if (string.IsNullOrEmpty(devUrl))
        {
            AnsiConsole.MarkupLine("[red]Dev URL is required. Please provide a dev URL using --dev <URL>.[/]");
            return null;
        }

        EnvironmentInfo? devEnv = await AnsiConsole.Status().StartAsync(
            $"Validating Development environment [bold]'{devUrl}'[/]...",
            ctx => PacUtils.GetEnvironmentInfoByUrlAsync(devUrl, settings.Verbose, cancellationToken));

        if (devEnv == null)
        {
            AnsiConsole.MarkupLine("[red]Invalid Dev environment. Please provide a valid Dataverse environment URL using --dev <URL>.[/]");
            return null;
        }

        if (devEnv.Type == "Production")
        {
            AnsiConsole.MarkupLine("[red]Dev environment must not be of type 'Production'.[/]");
            return null;
        }

        AnsiConsole.MarkupLine($"[green]Valid Dev environment: [bold]{devEnv.DisplayName}[/] ({devEnv.EnvironmentUrl}, Type: {devEnv.Type})[/]");
        return devEnv;
    }

    protected async Task<ProjectSolution?> ResolveAndValidateSolutionAsync(string? inputName, string environmentUrl, bool includeManaged, TSettings settings, CancellationToken cancellationToken)
    {
        var sln = Config!.GetOrUpdateSolution(inputName, includeManaged, settings);
        if (sln == null)
        {
            AnsiConsole.MarkupLine("[red]Solution could not be resolved. Please provide a solution name.[/]");
            return null;
        }

        List<SolutionInfo> solutions = await AnsiConsole.Status().StartAsync(
            $"Validating solution [bold]'{sln.Name}'[/]...",
            ctx => PacUtils.GetSolutionsAsync(environmentUrl, settings.Verbose, cancellationToken));

        var remoteSolution = solutions.FirstOrDefault(s => s.SolutionUniqueName?.Equals(sln.Name, StringComparison.OrdinalIgnoreCase) == true);
        if (remoteSolution == null)
        {
            AnsiConsole.MarkupLine($"[red]Solution '{sln.Name}' not found in environment '{environmentUrl}'.[/]");
            return null;
        }

        if (remoteSolution.IsManaged && !sln.IncludeManaged)
        {
            AnsiConsole.MarkupLine($"[red]Solution '{sln.Name}' is managed and --managed is not specified. We need dev environment to clone unmanaged artifacts.[/]");
            return null;
        }

        AnsiConsole.MarkupLine($"[green]Valid Solution: [bold]'{sln.Name}'[/] ({remoteSolution.SolutionUniqueName}, Managed: {remoteSolution.IsManaged})[/]");

        return sln;
    }
}
