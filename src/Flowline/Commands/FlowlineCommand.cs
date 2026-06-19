using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Services;
using Flowline.Utils;
using Flowline.Validation;
using Microsoft.PowerPlatform.Dataverse.Client;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public enum EnvironmentRole { Prod, Uat, Test, Dev }

public abstract class FlowlineCommand<TSettings>(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService) : AsyncCommand<TSettings>
    where TSettings : FlowlineSettings
{
    protected const string AllSolutionsFolderName = "solutions";
    protected const string PackageName = "Package";
    protected const string WebResourcesName = "WebResources";
    protected const string PluginsName = "Plugins";

    public static string PackageFolder(string slnFolder) => Path.Combine(slnFolder, PackageName);

    public static string FormatDuration(TimeSpan elapsed) =>
        elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
            : $"{(int)elapsed.TotalSeconds}s";

    protected readonly IAnsiConsole Console = console;
    protected FlowlineRuntimeOptions RuntimeOptions { get; } = runtimeOptions;

    protected string RootFolder { get; private set; } = Directory.GetCurrentDirectory();
    protected ProjectConfig? Config { get; private set; }
    protected virtual bool ShowWelcome => true;

    protected void InitializeRuntimeOptions(TSettings settings)
    {
        RuntimeOptions.IsVerbose = settings.Verbose;
        RuntimeOptions.Force = settings.Force;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, TSettings settings, CancellationToken cancellationToken)
    {
        InitializeRuntimeOptions(settings);

        if (ShowWelcome && ConsoleHelper.IsInteractive(settings) && FlowlineValidator.Default.ShouldShowWelcomeScreen(settings.NoCache))
            ConsoleHelper.WelcomeScreen(Console);

        await CheckSetupAsync(settings, cancellationToken);

        Config = ProjectConfig.Load(RootFolder) ?? new ProjectConfig();

        return await ExecuteFlowlineAsync(context, settings, cancellationToken);
    }

    protected abstract Task<int> ExecuteFlowlineAsync(CommandContext context, TSettings settings, CancellationToken cancellationToken);

    protected virtual async Task CheckSetupAsync(TSettings settings, CancellationToken cancellationToken)
    {
        await Console.Status().FlowlineSpinner().StartAsync("Checking your setup...", async ctx =>
        {
            await FlowlineValidator.Default.EnsureDotNetAsync(settings, cancellationToken);
            await FlowlineValidator.Default.EnsurePacCliAsync(settings, cancellationToken);
            await FlowlineValidator.Default.EnsureGitAsync(settings, cancellationToken);
            await FlowlineValidator.Default.EnsureGitRepoAsync(RootFolder, settings, cancellationToken);
        });

        Console.Ok("Prerequisites all good, let's go!");
    }

    protected async Task<EnvironmentInfo> GetAndCheckEnvironmentInfoAsync(EnvironmentRole role, string? inputUrl, TSettings settings, CancellationToken cancellationToken)
    {
        var label = role switch
        {
            EnvironmentRole.Prod => "Prod",
            EnvironmentRole.Uat  => "UAT",
            EnvironmentRole.Test => "Test",
            EnvironmentRole.Dev  => "Dev",
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };
        var flag = role switch
        {
            EnvironmentRole.Prod => "--prod",
            EnvironmentRole.Uat  => "--uat",
            EnvironmentRole.Test => "--test",
            EnvironmentRole.Dev  => "--dev",
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };

        var url = role switch
        {
            EnvironmentRole.Prod => Config!.GetOrUpdateProdUrl(inputUrl, settings),
            EnvironmentRole.Uat  => Config!.GetOrUpdateUatUrl(inputUrl, settings),
            EnvironmentRole.Test => Config!.GetOrUpdateTestUrl(inputUrl, settings),
            EnvironmentRole.Dev  => Config!.GetOrUpdateDevUrl(inputUrl, settings),
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };

        if (string.IsNullOrEmpty(url))
            throw new FlowlineException(ExitCode.ConfigInvalid, $"{label} URL is required — use {flag} <URL>.");

        EnvironmentInfo? env = await Console.Status().FlowlineSpinner().StartAsync(
            $"Checking {label.ToLower()} [bold]{url}[/]...",
            ctx => FlowlineValidator.Default.GetEnvironmentInfoByUrlAsync(url, settings, cancellationToken));

        if (env == null)
            throw new FlowlineException(ExitCode.ConnectionFailed, $"{label} environment not found — check the URL or your PAC login.");

        if (role == EnvironmentRole.Prod && env.Type != "Production")
            throw new FlowlineException(ExitCode.ValidationFailed, "That environment isn't Production type.");

        if (role != EnvironmentRole.Prod && env.Type == "Production")
            throw new FlowlineException(ExitCode.ValidationFailed, "That's a Production environment — use a sandbox or dev instead.");

        Console.Ok($"{label} env [bold]{env.DisplayName}[/] ({env.EnvironmentUrl}) exists");
        return env;
    }

    protected async Task<(IOrganizationServiceAsync2 Connection, PacProfile Profile)> ConnectToDataverseAsync(
        DataverseConnector dataverseConnector, string environmentUrl, CancellationToken cancellationToken)
    {
        PacProfile? resolvedProfile = null;
        IOrganizationServiceAsync2? conn = null;

        await Console.Status().FlowlineSpinner().StartAsync("Connecting to Dataverse...", async _ =>
        {
            resolvedProfile = await profileResolutionService.ResolveAsync(environmentUrl, cancellationToken);
            conn = await dataverseConnector.ConnectViaPacAsync(resolvedProfile, environmentUrl, cancellationToken);
        });

        Console.Ok("Connected to Dataverse");
        return (conn!, resolvedProfile!);
    }

    protected async Task<(ProjectSolution projectSolution, SolutionInfo solutionInfo)> GetAndCheckSolutionAsync(
        string? inputName,
        string environmentUrl,
        bool? includeManaged = null,
        TSettings settings = default!,
        CancellationToken cancellationToken = default)
    {
        var projectSln = Config!.GetOrUpdateSolution(inputName, includeManaged, settings);
        if (projectSln == null)
            throw new FlowlineException(ExitCode.ConfigInvalid, "Solution name is required — pass it as an argument or use --solution <name>.");

        SolutionInfo? remoteSln = await Console.Status().FlowlineSpinner().StartAsync(
            $"Looking up solution [bold]{projectSln.Name}[/]...",
            ctx => FlowlineValidator.Default.GetSolutionInfoAsync(environmentUrl, projectSln.Name, includeManaged ?? false, settings, cancellationToken));
        if (remoteSln == null)
            throw new FlowlineException(ExitCode.NotFound, $"Solution '{projectSln.Name}' not found in that environment.");

        Console.Ok($"Solution [bold]{projectSln.Name}[/] (managed: {remoteSln.IsManaged}) exists");

        return (projectSln, remoteSln);
    }
}
