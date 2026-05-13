using System.Reflection;
using Flowline.Config;
using Flowline.Core;
using Flowline.Utils;
using Flowline.Validation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public enum EnvironmentRole { Prod, Test, Dev }

public abstract class FlowlineCommand<TSettings> : AsyncCommand<TSettings> where TSettings : FlowlineSettings
{
    protected const string AllSolutionsFolderName = "solutions";
    protected const string WebResourcesName = "WebResources";
    protected const string PluginsName = "Plugins";
    protected const string MappingPacFileName = "MappingPac.xml";
    protected const string MappingBuildFileName = "MappingBuild.xml";

    public static string FormatDuration(TimeSpan elapsed) =>
        elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
            : $"{(int)elapsed.TotalSeconds}s";

    protected readonly IAnsiConsole Console;
    protected FlowlineRuntimeOptions RuntimeOptions { get; }

    protected FlowlineCommand(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions)
    {
        Console = console;
        RuntimeOptions = runtimeOptions;
    }

    protected string RootFolder { get; private set; } = Directory.GetCurrentDirectory();
    protected ProjectConfig? Config { get; private set; }
    protected virtual bool ShowWelcome => true;

    protected void InitializeRuntimeOptions(TSettings settings)
    {
        RuntimeOptions.IsVerbose = settings.Verbose;
        RuntimeOptions.JsonOutput = settings.JsonOutput;
        RuntimeOptions.Force = settings.Force;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, TSettings settings, CancellationToken cancellationToken)
    {
        InitializeRuntimeOptions(settings);

        if (ShowWelcome && !settings.JsonOutput)
            WelcomeScreen();

        await CheckSetupAsync(settings, cancellationToken);

        Config = ProjectConfig.Load(RootFolder) ?? new ProjectConfig();

        return await ExecuteFlowlineAsync(context, settings, cancellationToken);
    }

    void WelcomeScreen()
    {
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        var versionText = new Text($"Version {version}", new Style(Color.Green));

        Console.MarkupLine("[green]____ _    ____ _ _ _ _    _ _  _ ____[/]");
        Console.MarkupLine("[green]|___ |    |  | | | | |    | |\\ | |___[/]");
        Console.MarkupLine("[green]|    |___ |__| |_|_| |___ | | \\| |___[/]");
        Console.Write(versionText);
        Console.WriteLine();
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

        Console.Success("All good, let's go!");
    }

    protected async Task<EnvironmentInfo?> GetAndCheckEnvironmentInfoAsync(EnvironmentRole role, string? inputUrl, TSettings settings, CancellationToken cancellationToken)
    {
        var label = role switch
        {
            EnvironmentRole.Prod => "Prod",
            EnvironmentRole.Test => "Test",
            EnvironmentRole.Dev  => "Dev",
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };
        var flag = role switch
        {
            EnvironmentRole.Prod => "--prod",
            EnvironmentRole.Test => "--test",
            EnvironmentRole.Dev  => "--dev",
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };

        var url = role switch
        {
            EnvironmentRole.Prod => Config!.GetOrUpdateProdUrl(inputUrl, settings),
            EnvironmentRole.Test => Config!.GetOrUpdateTestUrl(inputUrl, settings),
            EnvironmentRole.Dev  => Config!.GetOrUpdateDevUrl(inputUrl, settings),
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };

        if (string.IsNullOrEmpty(url))
        {
            Console.Error($"{label} URL is required — use {flag} <URL>.");
            return null;
        }

        EnvironmentInfo? env = await Console.Status().FlowlineSpinner().StartAsync(
            $"Checking {label.ToLower()} [bold]{url}[/]...",
            ctx => FlowlineValidator.Default.GetEnvironmentInfoByUrlAsync(url, settings, cancellationToken));

        if (env == null)
        {
            Console.Error($"{label} environment not found — check the URL or your PAC login.");
            return null;
        }

        if (role == EnvironmentRole.Prod && env.Type != "Production")
        {
            Console.Error("That environment isn't Production type.");
            return null;
        }

        if (role != EnvironmentRole.Prod && env.Type == "Production")
        {
            Console.Error("That's a Production environment — use a sandbox or dev instead.");
            return null;
        }

        Console.Success($"{label}: [bold]{env.DisplayName}[/] ({env.EnvironmentUrl})");
        return env;
    }

    protected async Task<(ProjectSolution? projectSolution, SolutionInfo? solutionInfo)> GetAndCheckSolutionAsync(
        string? inputName,
        string environmentUrl,
        bool includeManaged,
        TSettings settings,
        CancellationToken cancellationToken,
        bool? useMapping = null)
    {
        var projectSln = Config!.GetOrUpdateSolution(inputName, includeManaged, settings, useMapping);
        if (projectSln == null)
        {
            Console.Error("Solution name is required — pass it as an argument or use --solution <name>.");
            return (null, null);
        }

        SolutionInfo? remoteSln = await Console.Status().FlowlineSpinner().StartAsync(
            $"Looking up solution [bold]{projectSln.Name}[/]...",
            ctx => FlowlineValidator.Default.GetSolutionInfoAsync(environmentUrl, projectSln.Name, includeManaged, settings, cancellationToken));
        if (remoteSln == null)
        {
            Console.Error($"Solution [bold]{projectSln.Name}[/] not found in that environment.");
            return (projectSln, null);
        }

        Console.Success($"Solution: [bold]{projectSln.Name}[/] (managed: {remoteSln.IsManaged})");

        return (projectSln, remoteSln);
    }
}
