using System.ComponentModel;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using Flowline.Utils;
using Spectre.Console;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class CloneCommand(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService, ILoggerFactory loggerFactory, SubprocessCapture capture,
    CreateSolutionService createSolutionService, CreateEnvironmentResolver createEnvironmentResolver, SolutionCreateFlow solutionCreateFlow) :
    FlowlineCommand<CloneCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture)
{
    /// <summary>Seam for testing — overrides ConsoleHelper.IsInteractive (global console capability
    /// check can't be driven by an injected TestConsole).</summary>
    internal Func<bool>? IsInteractiveOverride { get; set; }

    /// <summary>Seam for testing — overrides PacUtils.GetSolutionsAsync (shells out to a real pac.exe
    /// subprocess with no mocking seam of its own).</summary>
    internal Func<string, CancellationToken, Task<List<SolutionInfo>>>? GetSolutionsOverride { get; set; }

    /// <summary>Seam for testing — overrides the "create new" routing into <see cref="SolutionCreateFlow"/>
    /// (U5's own tests already cover the flow's internals; this only proves clone routes into it, per R2).</summary>
    internal Func<EnvironmentInfo, string, string, ProjectConfig, CancellationToken, Task<int>>? CreateFlowOverride { get; set; }

    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "[solution]")]
        [Description("Solution to clone into this repo (omit to pick or create one interactively)")]
        public string? Solution { get; set; }

        [CommandOption("--prod <URL>")]
        [Description("Production environment URL to clone solution from")]
        public string? ProdUrl { get; set; }

        [CommandOption("--uat <URL>")]
        [Description("UAT environment URL to clone solution from")]
        public string? UatUrl { get; set; }

        [CommandOption("--test <URL>")]
        [Description("Test environment URL to clone solution from")]
        public string? TestUrl { get; set; }

        [CommandOption("--dev <URL>")]
        [Description("Development environment URL to clone solution from")]
        public string? DevUrl { get; set; }

        [CommandOption("--managed [false]")]
        [Description("Include managed artifacts (--managed false resets to default)")]
        [DefaultValue(true)]
        public FlagValue<bool> IncludeManaged { get; set; } = null!;
    }

    protected override bool RequiresProject => false;
    protected override string[] ValidForceSpecifiers => FlowlineSettings.ConfigOnlyValidSpecifiers;

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Save all provided URLs to config first (no API calls, just config update + prompt on conflict)
        Config!.GetOrUpdateProdUrl(settings.ProdUrl, settings);
        Config!.GetOrUpdateUatUrl(settings.UatUrl, settings);
        Config!.GetOrUpdateTestUrl(settings.TestUrl, settings);
        Config!.GetOrUpdateDevUrl(settings.DevUrl, settings);

        EnvironmentInfo sourceEnv;
        ProjectSolution projectSln;
        SolutionInfo solutionInfo;

        // U6/R2/R11/R17: no solution named, no role URL configured (this run's flags or a prior
        // .flowline), interactive session — offer pick-existing-or-create-new instead of
        // FindUnmanagedSourceAsync's flag-driven error. Gated on all three so the existing
        // flag-driven path (solution named, or any role URL configured) behaves exactly as today (R13):
        // a non-interactive run always falls through to FindUnmanagedSourceAsync, so it raises the
        // same NotFound error it always has, never CreateEnvironmentResolver's differently-worded one.
        if (ShouldPickOrCreate(settings, Config, IsInteractive()))
        {
            var (createExitCode, pickedEnv, pickedSln, pickedInfo) = await PickOrCreateAsync(settings, RootFolder, Config, cancellationToken);
            if (createExitCode is { } pickExitCode)
                return pickExitCode;

            (sourceEnv, projectSln, solutionInfo) = (pickedEnv!, pickedSln!, pickedInfo!);
        }
        else
        {
            (sourceEnv, projectSln, solutionInfo) = await FindUnmanagedSourceAsync(settings, cancellationToken);
        }

        Logger.LogInformation("source={EnvironmentUrl} solution={SolutionName}", sourceEnv.EnvironmentUrl, projectSln.UniqueName);

        // Before anything is written: the solution name becomes a C# namespace in the scaffolded plugin
        // project, and a keyword there produces source that doesn't compile.
        if (CreateSolutionService.DescribeCSharpKeywordCollision(projectSln.UniqueName) is { } keywordCollision)
            throw new FlowlineException(ExitCode.ValidationFailed, keywordCollision);

        Config.Save();
        Console.Verbose($"Project configuration saved to {ProjectConfig.s_configFileName}");

        var slnFolder = RootFolder;
        var solutionName = projectSln.UniqueName;

        var cdsprojPath = Path.Combine(CreateSolutionService.ScaffoldedDataverseSolutionFolder(slnFolder), $"{solutionName}.cdsproj");
        var slnFilePath = CreateSolutionService.ResolveSolutionFilePath(slnFolder, solutionName);
        var slnFileName = Path.GetFileName(slnFilePath);

        await createSolutionService.CloneSolutionFromDataverseAsync(projectSln, slnFolder, cdsprojPath, sourceEnv.EnvironmentUrl!, cancellationToken);
        await createSolutionService.CreateSolutionFileAsync(slnFolder, slnFilePath, cdsprojPath, cancellationToken);

        // The .cdsproj entry CreateSolutionFileAsync just wrote makes the solution file loadable, so the
        // scaffold-skip checks below can ask "is a plugin/WebResources project already registered under
        // any name or location" instead of only "does the default folder hold one" — a project whose
        // Plugins/WebResources project was legitimately moved/renamed (project-structure flexibility)
        // resolves here the same way push/sync/deploy already discover it. Loaded once and reused by both
        // setup calls, matching SolutionFileLayout's one-read contract.
        var layout = await SolutionFileLayout.LoadAsync(slnFolder, cancellationToken);
        await createSolutionService.SetupPluginsProjectAsync(slnFolder, slnFilePath, solutionName, layout, cancellationToken);
        var webresourcesFolder = await createSolutionService.SetupWebResourcesProjectAsync(slnFolder, slnFilePath, solutionName, layout, cancellationToken);
        createSolutionService.SeedWebResourceDistFromSrc(slnFolder, webresourcesFolder, solutionInfo.PublisherPrefix, projectSln.UniqueName);

        createSolutionService.ScaffoldRootGitignore(slnFolder);

        if (await ValidatePackAndBuildAsync(projectSln, CreateSolutionService.ScaffoldedDataverseSolutionFolder(slnFolder), slnFolder,
                buildRelease: true, skipBuild: false, cancellationToken) is { } exitCode)
        {
            return exitCode;
        }

        await createSolutionService.ScaffoldAgentsFileAsync(slnFolder, projectSln.UniqueName, slnFileName, cancellationToken);
        await createSolutionService.ScaffoldClaudeFileAsync(slnFolder, cancellationToken);
        await new DataverseContextGenerator(Console).GenerateAsync(
            Path.Combine(CreateSolutionService.ScaffoldedDataverseSolutionFolder(slnFolder), "src"), projectSln.UniqueName, RootFolder, cancellationToken);

        Console.Done("Cloned! Use 'push' and 'sync' to keep it in flow. ヽ(•‿•)ノ");
        return 0;
    }

    private async Task<(EnvironmentInfo sourceEnv, ProjectSolution projectSolution, SolutionInfo solutionInfo)> FindUnmanagedSourceAsync(Settings settings,
        CancellationToken cancellationToken)
    {
        foreach (var role in new[] { EnvironmentRole.Prod, EnvironmentRole.Uat, EnvironmentRole.Test, EnvironmentRole.Dev })
        {
            var configUrl = role switch
            {
                EnvironmentRole.Prod => Config!.ProdUrl,
                EnvironmentRole.Uat  => Config!.UatUrl,
                EnvironmentRole.Test => Config!.TestUrl,
                EnvironmentRole.Dev  => Config!.DevUrl,
                _ => null
            };
            if (string.IsNullOrEmpty(configUrl)) continue;

            var (env, _) = await GetAndCheckEnvironmentInfoAsync(role, null, settings, cancellationToken);
            var (sln, info) = await GetAndCheckSolutionAsync(
                settings.Solution, env.EnvironmentUrl!, settings.IncludeManaged.IsSet ? settings.IncludeManaged.Value : (bool?)null, settings, cancellationToken);

            if (info.IsManaged)
            {
                var label = role switch { EnvironmentRole.Prod => "Prod", EnvironmentRole.Uat => "UAT", EnvironmentRole.Test => "Test", _ => "Dev" };
                Console.MarkupLine($"[dim]{label} solution is managed — skipping[/]");
                continue;
            }

            return (env, sln, info);
        }

        throw new FlowlineException(ExitCode.NotFound, "No unmanaged environment found — provide a --dev, --test, --uat, or --prod URL with an unmanaged solution.");
    }

    // U6/R13: pure — no I/O — so the gate itself is directly testable without a TestConsole or a
    // fully-constructed command. Interactivity is passed in rather than read here so callers (and
    // tests) control it explicitly instead of this reaching for the global console check.
    internal static bool ShouldPickOrCreate(Settings settings, ProjectConfig config, bool isInteractive) =>
        string.IsNullOrWhiteSpace(settings.Solution) && isInteractive && !AnyRoleUrlConfigured(config);

    static bool AnyRoleUrlConfigured(ProjectConfig config) =>
        !string.IsNullOrWhiteSpace(config.ProdUrl) || !string.IsNullOrWhiteSpace(config.UatUrl) ||
        !string.IsNullOrWhiteSpace(config.TestUrl) || !string.IsNullOrWhiteSpace(config.DevUrl);

    // U6: the pick-existing-or-create-new menu. Takes rootFolder/config explicitly (not RootFolder/
    // Config) so it's callable — and testable — without running the base command pipeline that
    // normally sets them, the same reason SolutionCreateFlow.RunAsync takes them as parameters.
    internal async Task<(int? ExitCode, EnvironmentInfo? Env, ProjectSolution? ProjectSolution, SolutionInfo? SolutionInfo)> PickOrCreateAsync(
        Settings settings, string rootFolder, ProjectConfig config, CancellationToken cancellationToken)
    {
        var devEnv = await createEnvironmentResolver.ResolveAsync(settings.DevUrl, settings, cancellationToken);

        var getSolutions = GetSolutionsOverride ?? ((url, ct) => PacUtils.GetSolutionsAsync(url, _capture, ct));
        var allSolutions = await Console.Status().FlowlineSpinner().StartAsync(
            $"Checking solutions in [bold]{devEnv.DisplayName}[/]...",
            _ => getSolutions(devEnv.EnvironmentUrl!, cancellationToken));

        // R11: unmanaged only — clone doesn't support managed sources — with a note of how many were hidden.
        var unmanaged = allSolutions.Where(s => !s.IsManaged).ToList();
        var hiddenManagedCount = allSolutions.Count - unmanaged.Count;
        if (hiddenManagedCount > 0)
            Console.Info($"{hiddenManagedCount} managed solution{(hiddenManagedCount == 1 ? "" : "s")} hidden — clone supports unmanaged only.");

        // (Label, Solution) rather than SelectionPrompt<SolutionInfo?> — SelectionPrompt<T> requires
        // T : notnull, and the create-new choice has no solution to hang off (mirrors
        // SolutionCreateFlow.PickPublisherPrefixAsync's (Label, Prefix) choice).
        const string createNewLabel = "[italic]+ Create new solution[/]";
        var choices = unmanaged
            .Select(s => (Label: $"{s.SolutionUniqueName} — {s.FriendlyName}", Solution: (SolutionInfo?)s))
            .Append((Label: createNewLabel, Solution: (SolutionInfo?)null))
            .ToList();

        var prompt = new SelectionPrompt<(string Label, SolutionInfo? Solution)>()
            .Title(FlowlineConsoleExtensions.Question("Pick a solution to clone, or create a new one:"))
            .UseConverter(c => c.Label)
            .AddChoices(choices);

        var selected = await Console.PromptAsync(prompt, cancellationToken);

        if (selected.Solution is null)
        {
            // R2: routes into the same orchestrator `init` uses — clone has no positional name in
            // this branch (that's exactly what makes it reach the picker), so it asks for one.
            var uniqueName = await Console.PromptAsync(new TextPrompt<string>(FlowlineConsoleExtensions.Question("Solution unique name:")), cancellationToken);

            var createFlow = CreateFlowOverride ?? ((env, name, root, cfg, ct) =>
                solutionCreateFlow.RunAsync(env, name, null, null, null, root, cfg,
                    (projectSln, dataverseSolutionFolder, slnFolder, ct2) =>
                        ValidatePackAndBuildAsync(projectSln, dataverseSolutionFolder, slnFolder, buildRelease: true, skipBuild: false, ct2),
                    ct));

            var createExitCode = await createFlow(devEnv, uniqueName, rootFolder, config, cancellationToken);
            return (createExitCode, null, null, null);
        }

        // R17: confirm which .flowline role this env saves under — Dev listed first so Enter alone
        // accepts it, and the gate above only reaches here when no role is configured yet, so that
        // default is unconditional.
        var role = await PickRoleAsync(cancellationToken);
        _ = role switch
        {
            EnvironmentRole.Dev  => config.GetOrUpdateDevUrl(devEnv.EnvironmentUrl, settings),
            EnvironmentRole.Test => config.GetOrUpdateTestUrl(devEnv.EnvironmentUrl, settings),
            EnvironmentRole.Uat  => config.GetOrUpdateUatUrl(devEnv.EnvironmentUrl, settings),
            EnvironmentRole.Prod => config.GetOrUpdateProdUrl(devEnv.EnvironmentUrl, settings),
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };
        var roleLabel = role switch { EnvironmentRole.Dev => "DEV", EnvironmentRole.Test => "TEST", EnvironmentRole.Uat => "UAT", EnvironmentRole.Prod => "PROD", _ => role.ToString() };
        Console.Ok($"{roleLabel} set to [bold]{devEnv.DisplayName}[/] ({devEnv.EnvironmentUrl})");

        var projectSln = config.GetOrUpdateSolution(selected.Solution.SolutionUniqueName,
            settings.IncludeManaged.IsSet ? settings.IncludeManaged.Value : (bool?)null, settings)!;

        return (null, devEnv, projectSln, selected.Solution);
    }

    async Task<EnvironmentRole> PickRoleAsync(CancellationToken cancellationToken)
    {
        // (Label, Role) rather than SelectionPrompt<EnvironmentRole> directly — same reason as the
        // solution/publisher pickers (SolutionCreateFlow.PickPublisherPrefixAsync): a bare Enter must
        // land on whatever's listed first, and a raw enum choice risks resolving to default(T) instead.
        var choices = new (string Label, EnvironmentRole Role)[]
        {
            ("Dev", EnvironmentRole.Dev),
            ("Test", EnvironmentRole.Test),
            ("UAT", EnvironmentRole.Uat),
            ("Prod", EnvironmentRole.Prod),
        };
        var prompt = new SelectionPrompt<(string Label, EnvironmentRole Role)>()
            .Title(FlowlineConsoleExtensions.Question("Save this environment under which .flowline role?"))
            .UseConverter(c => c.Label)
            .AddChoices(choices);
        return (await Console.PromptAsync(prompt, cancellationToken)).Role;
    }

    bool IsInteractive() => IsInteractiveOverride?.Invoke() ?? ConsoleHelper.IsInteractive(settings: null);
}
