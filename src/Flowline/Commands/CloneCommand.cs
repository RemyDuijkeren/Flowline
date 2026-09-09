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
    ProjectScaffolder projectScaffolder, CreateEnvironmentResolver createEnvironmentResolver, NuGetVersionClient nuGetVersionClient, EnvironmentTargetResolver environmentTargetResolver) :
    FlowlineCommand<CloneCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture, nuGetVersionClient)
{
    /// <summary>Seam for testing — overrides PacUtils.GetSolutionsAsync (shells out to a real pac.exe
    /// subprocess with no mocking seam of its own).</summary>
    internal Func<string, CancellationToken, Task<List<SolutionInfo>>>? GetSolutionsOverride { get; set; }

    public sealed class Settings : EnvironmentSettings
    {
        [CommandArgument(0, "[solution]")]
        [Description("Solution to clone into this repo (omit to pick one interactively)")]
        public string? Solution { get; set; }

        [CommandOption("--managed [true|false]")]
        [Description("Include managed artifacts — --managed alone means true, --managed false means false (saved to .flowline)")]
        [DefaultValue(true)]
        public FlagValue<bool> IncludeManaged { get; set; } = null!;
    }

    protected override bool RequiresFlowlineProject => false;
    protected override string[] ValidForceSpecifiers => FlowlineSettings.ConfigOnlyValidSpecifiers;

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        EnvironmentInfo sourceEnv;
        ProjectSolution projectSln;
        SolutionInfo solutionInfo;

        // U6/R11/R17: no solution named, no --env, no role URL configured (a prior .flowline),
        // interactive session — offer the environment + solution pickers instead of
        // ResolveConfiguredSourceAsync's flag-driven error. Gated on all four so the existing
        // flag-driven path (solution named, --env given, or any role URL configured) behaves exactly
        // as today (R13): a non-interactive run always falls through to ResolveConfiguredSourceAsync,
        // so it raises the same NotFound error it always has, never CreateEnvironmentResolver's
        // differently-worded one.
        if (ShouldPickSolution(settings, Config, IsInteractive()))
        {
            var (pickExitCodeOrNull, pickedEnv, pickedSln, pickedInfo) = await PickSolutionAsync(settings, Config, cancellationToken);
            if (pickExitCodeOrNull is { } pickExitCode)
                return pickExitCode;

            (sourceEnv, projectSln, solutionInfo) = (pickedEnv!, pickedSln!, pickedInfo!);
        }
        else
        {
            (sourceEnv, projectSln, solutionInfo) = await ResolveConfiguredSourceAsync(settings, Config!, cancellationToken);
        }

        Logger.LogInformation("source={EnvironmentUrl} solution={SolutionName}", sourceEnv.EnvironmentUrl, projectSln.UniqueName);

        // Before anything is written: the solution name becomes a C# namespace in the scaffolded plugin
        // project, and a keyword there produces source that doesn't compile.
        if (ProjectScaffolder.DescribeCSharpKeywordCollision(projectSln.UniqueName) is { } keywordCollision)
            throw new FlowlineException(ExitCode.ValidationFailed, keywordCollision);

        Config.Save();
        Console.Verbose($"Project configuration saved to {ProjectConfig.s_configFileName}");

        var slnFolder = RootFolder;
        var solutionName = projectSln.UniqueName;

        var slnFileName = await projectScaffolder.ScaffoldProjectAsync(projectSln, slnFolder, sourceEnv.EnvironmentUrl!,
            solutionInfo.PublisherPrefix, cancellationToken);

        if (await ValidatePackAndBuildAsync(projectSln, ProjectScaffolder.ScaffoldedDataverseSolutionFolder(slnFolder), slnFolder,
                buildRelease: true, skipBuild: false, cancellationToken) is { } exitCode)
        {
            return exitCode;
        }

        await projectScaffolder.ScaffoldDocsAsync(slnFolder, solutionName, slnFileName, cancellationToken);

        Console.Done("Cloned! Use 'push' and 'pull' to keep it in flow. ヽ(•‿•)ノ");
        return 0;
    }

    // R9: the flag-driven path — --env names the source directly; with no --env, exactly one
    // configured role URL is unambiguous, more than one needs a pick (KD4), and zero is the same
    // NotFound today's flag-driven path always raised (ShouldPickSolution already routes a blank,
    // zero-configured, interactive run to the picker above instead of here).
    private async Task<(EnvironmentInfo sourceEnv, ProjectSolution projectSolution, SolutionInfo solutionInfo)> ResolveConfiguredSourceAsync(
        Settings settings, ProjectConfig config, CancellationToken cancellationToken)
    {
        var (sourceUrl, role) = await ResolveSourceUrlAsync(settings, config, cancellationToken);

        var (env, _) = await GetAndCheckEnvironmentAsync(sourceUrl, role, settings, cancellationToken);
        var (sln, info) = await GetAndCheckSolutionAsync(
            settings.Solution, env.EnvironmentUrl!, settings.IncludeManaged.IsSet ? settings.IncludeManaged.Value : (bool?)null, settings, cancellationToken);

        // No fallback to another role now that there's a single, explicitly chosen source (R9) — a
        // managed-only environment fails naming it, rather than silently skipping to another one.
        if (info.IsManaged)
            throw new FlowlineException(ExitCode.NotFound, $"'{sln.UniqueName}' in '{env.DisplayName}' is managed — clone needs an unmanaged source.");

        return (env, sln, info);
    }

    // Pure URL/role decision — no I/O beyond the resolver itself and the role picker prompt, kept
    // separate from ResolveConfiguredSourceAsync (which also checks the environment and solution exist,
    // needing a real pac subprocess) so it's directly testable. config is explicit, not Config, for the
    // same reason PickSolutionAsync takes it explicitly — callable without the base pipeline.
    internal async Task<(string Url, EnvironmentRole? Role)> ResolveSourceUrlAsync(Settings settings, ProjectConfig config, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(settings.Env))
        {
            var target = await environmentTargetResolver.ResolveAsync(settings.Env, config, onlyRole: null, IsInteractive(), settings,
                (url, ct) => Validator.GetEnvironmentInfoByUrlAsync(url, settings, settings.NoCache, ct), cancellationToken);
            return (target.Url, target.Role);
        }

        var configuredRoles = ConfiguredRoles(config);
        switch (configuredRoles.Count)
        {
            case 1:
                var onlyRole = configuredRoles[0];
                return (config.GetUrl(onlyRole)!, onlyRole);

            case > 1 when !IsInteractive():
                throw new FlowlineException(ExitCode.ValidationFailed,
                    "More than one environment is configured — pass --env <dev|test|uat|prod> to say which one to clone from.");

            case > 1:
                var pickedRole = await PickConfiguredRoleAsync(configuredRoles, cancellationToken);
                return (config.GetUrl(pickedRole)!, pickedRole);

            default:
                throw new FlowlineException(ExitCode.NotFound, "No environment configured — pass --env <url> to say which one to clone from.");
        }
    }

    // U4: pure — no I/O — which .flowline roles already have a URL, in Dev/Test/Uat/Prod order.
    internal static List<EnvironmentRole> ConfiguredRoles(ProjectConfig config) =>
        EnvironmentRoles.All
            .Where(r => !string.IsNullOrWhiteSpace(config.GetUrl(r)))
            .ToList();

    async Task<EnvironmentRole> PickConfiguredRoleAsync(List<EnvironmentRole> configuredRoles, CancellationToken cancellationToken)
    {
        var choices = configuredRoles
            .Select(r => (Label: RoleLabel(r), Role: r))
            .ToList();
        var prompt = new SelectionPrompt<(string Label, EnvironmentRole Role)>()
            .Title(FlowlineConsoleExtensions.Question("More than one environment is configured — pick which one to clone from:"))
            .UseConverter(c => c.Label)
            .AddChoices(choices);
        return (await Console.PromptAsync(prompt, cancellationToken)).Role;
    }

    // U6/R13: pure — no I/O — so the gate itself is directly testable without a TestConsole or a
    // fully-constructed command. Interactivity is passed in rather than read here so callers (and
    // tests) control it explicitly instead of this reaching for the global console check.
    internal static bool ShouldPickSolution(Settings settings, ProjectConfig config, bool isInteractive) =>
        string.IsNullOrWhiteSpace(settings.Solution) && string.IsNullOrWhiteSpace(settings.Env) && isInteractive && !AnyRoleUrlConfigured(config);

    // The environment's Default solution is the catch-all every unmanaged component lands in — it's the
    // environment, not a project, so it never belongs in the picker. Naming it explicitly
    // (`flowline clone Default`) still works; this only shapes what the picker offers.
    static bool IsDefaultSolution(SolutionInfo solution) =>
        string.Equals(solution.SolutionUniqueName, "Default", StringComparison.OrdinalIgnoreCase);

    static bool AnyRoleUrlConfigured(ProjectConfig config) =>
        !string.IsNullOrWhiteSpace(config.ProdUrl) || !string.IsNullOrWhiteSpace(config.UatUrl) ||
        !string.IsNullOrWhiteSpace(config.TestUrl) || !string.IsNullOrWhiteSpace(config.DevUrl);

    // U6: the environment + solution pickers. Takes config explicitly (not Config) so it's callable —
    // and testable — without running the base command pipeline that normally sets it.
    internal async Task<(int? ExitCode, EnvironmentInfo? Env, ProjectSolution? ProjectSolution, SolutionInfo? SolutionInfo)> PickSolutionAsync(
        Settings settings, ProjectConfig config, CancellationToken cancellationToken, string? seedSourceUrl = null)
    {
        // Env-first (source-of-truth model): pick the environment to clone from, then its unmanaged
        // solutions. Zero unmanaged means it's likely not the source of truth (managed-only PROD, or the
        // wrong env) — guide and let the user re-pick. A flag-specified URL can't be re-picked, so fall
        // through to the stop below instead of looping. seedSourceUrl only ever arrives from a test —
        // ShouldPickSolution already requires --env to be blank before this runs, so a real run always
        // starts the tenant-wide picker (null).
        EnvironmentInfo devEnv;
        List<SolutionInfo> unmanaged;
        while (true)
        {
            devEnv = await createEnvironmentResolver.ResolveSourceAsync(seedSourceUrl, settings, cancellationToken);

            var getSolutions = GetSolutionsOverride ?? ((url, ct) => PacUtils.GetSolutionsAsync(url, _capture, ct));
            var allSolutions = await Console.Status().FlowlineSpinner().StartAsync(
                $"Checking solutions in [bold]{devEnv.DisplayName}[/]...",
                _ => getSolutions(devEnv.EnvironmentUrl!, cancellationToken));

            // R11: unmanaged only — clone doesn't support managed sources — with a note of how many were hidden.
            unmanaged = allSolutions.Where(s => !s.IsManaged && !IsDefaultSolution(s)).ToList();
            var hiddenManagedCount = allSolutions.Count(s => s.IsManaged);
            if (hiddenManagedCount > 0)
                Console.Info($"{hiddenManagedCount} managed solution{(hiddenManagedCount == 1 ? "" : "s")} hidden — clone supports unmanaged only.");

            if (unmanaged.Count > 0)
                break;

            Console.Info("No unmanaged solutions here — Flowline's source of truth is usually PROD with unmanaged. Pick the environment that holds yours.");
            if (!string.IsNullOrWhiteSpace(seedSourceUrl) || !IsInteractive())
                break; // can't re-pick a flag-specified env, or no TTY — stop instead of looping
        }

        // Creating a solution is `init`'s job, not clone's — clone adopts what's already in Dataverse.
        // So an environment with nothing to adopt is the end of this command, not a menu with a
        // create-new escape hatch (which, on a PROD source, would only fail the DEV-only create guard).
        if (unmanaged.Count == 0)
        {
            Console.CannotContinue(
                $"Nothing to clone in '{devEnv.DisplayName}' — no unmanaged solution there.",
                "Run 'flowline init <name>' to create one in DEV, or re-run 'flowline clone' and pick the environment that holds your solution.");
            return (0, null, null, null);
        }

        var prompt = new SelectionPrompt<SolutionInfo>()
            .Title(FlowlineConsoleExtensions.Question("Pick a solution to clone:"))
            .UseConverter(s => $"{s.SolutionUniqueName} — {s.FriendlyName}")
            .AddChoices(unmanaged);

        var selected = await Console.PromptAsync(prompt, cancellationToken);

        // R17: assign the .flowline role from the source env's type. Type-driven, not a free pick — the
        // source-of-truth model means a Production source is the Prod role; the gate above only reaches
        // here when no role is configured yet.
        var role = await ResolveRoleAsync(devEnv, cancellationToken);
        config.GetOrUpdateUrl(role, devEnv.EnvironmentUrl, settings);
        Console.Ok($"{role.UpperLabel()} set to [bold]{devEnv.DisplayName}[/] ({devEnv.EnvironmentUrl})");

        var projectSln = config.GetOrUpdateSolution(selected.SolutionUniqueName,
            settings.IncludeManaged.IsSet ? settings.IncludeManaged.Value : (bool?)null, settings)!;

        return (null, devEnv, projectSln, selected);
    }

    // Type-driven role (R17, source-of-truth model): only a real Production env holds the Prod role; a
    // Developer env is always Dev; everything else (Sandbox, or any other/unknown type) prompts among the
    // non-prod roles, defaulting Dev — Prod is never offered here because only a Production-typed env earns it.
    async Task<EnvironmentRole> ResolveRoleAsync(EnvironmentInfo env, CancellationToken cancellationToken)
    {
        if (string.Equals(env.Type, "Production", StringComparison.OrdinalIgnoreCase))
            return EnvironmentRole.Prod;
        if (string.Equals(env.Type, "Developer", StringComparison.OrdinalIgnoreCase))
            return EnvironmentRole.Dev;

        // (Label, Role) rather than SelectionPrompt<EnvironmentRole> directly — a bare Enter must land on
        // whatever's listed first, and a raw enum choice risks resolving to default(T) instead.
        var choices = new (string Label, EnvironmentRole Role)[]
        {
            ("Dev", EnvironmentRole.Dev),
            ("Test", EnvironmentRole.Test),
            ("UAT", EnvironmentRole.Uat),
        };
        var prompt = new SelectionPrompt<(string Label, EnvironmentRole Role)>()
            .Title(FlowlineConsoleExtensions.Question($"Save [bold]{env.DisplayName}[/] under which .flowline role?"))
            .UseConverter(c => c.Label)
            .AddChoices(choices);
        return (await Console.PromptAsync(prompt, cancellationToken)).Role;
    }

}
