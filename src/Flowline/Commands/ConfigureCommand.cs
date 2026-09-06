using System.ComponentModel;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Configure;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Infrastructure;
using Flowline.Services;
using Flowline.Utils;
using Flowline.Validation;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

/// <summary>Applies a per-environment settings file to a Dataverse environment, or captures one.</summary>
public class ConfigureCommand(
    IAnsiConsole console,
    DataverseConnector dataverseConnector,
    FlowlineRuntimeOptions runtimeOptions,
    ProfileResolutionService profileResolutionService,
    ILoggerFactory loggerFactory,
    SubprocessCapture capture,
    NuGetVersionClient nuGetVersionClient)
    : FlowlineCommand<ConfigureCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture, nuGetVersionClient)
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "<target>")]
        [Description("Target environment: prod, uat, test, dev, or a URL")]
        public string Target { get; set; } = null!;

        [CommandOption("--settings-file <path>")]
        [Description("Apply this settings file instead of the one beside the .cdsproj")]
        public string? SettingsFile { get; set; }

        [CommandOption("--solution-name <name>")]
        [Description("Solution unique name — required outside a Flowline project, rejected inside one")]
        public string? SolutionName { get; set; }

        // No [DefaultValue] here, unlike the FlagValue<bool> options on clone and sync: Spectre assigns the
        // declared default straight into FlagValue<T>.Value, so a `false` default on a FlagValue<string>
        // throws InvalidCastException while binding, before the command ever runs. An unset flag is
        // IsSet == false with a null Value, which is exactly what this needs.
        [CommandOption("--pull [zip-or-folder]")]
        [Description("Capture the environment's configuration into the settings file instead of applying it")]
        public FlagValue<string> Pull { get; set; } = null!;

        [CommandOption("--dry-run")]
        [Description("Report what would change and write nothing")]
        [DefaultValue(false)]
        public bool DryRun { get; set; } = false;
    }

    // KTD10: configure writes only what the file declares, so there is no destructive scope to gate. The
    // vocabulary is deliberately empty of configure-specific specifiers rather than absent — recorded here so
    // nobody later invents a PROD confirmation and breaks every CI job already running the command.
    protected override string[] ValidForceSpecifiers => FlowlineSettings.ConfigOnlyValidSpecifiers;

    protected override bool IsStandalone(Settings settings) =>
        ResolveStandalone(settings.SolutionName, Directory.GetCurrentDirectory());

    /// <summary>
    /// Stand-alone is an explicit solution name plus no project to read one from (KTD3).
    /// </summary>
    /// <remarks>
    /// Same shape as <see cref="DeployCommand.ResolveStandalone"/> — a flag plus the absence of a project —
    /// rather than push's flag-only-then-throw form, so the two precedents do not diverge further. Pure so
    /// the rule is testable without a checkout.
    /// </remarks>
    internal static bool ResolveStandalone(string? solutionName, string startDir) =>
        !string.IsNullOrWhiteSpace(solutionName) && FindFlowlineProjectRoot(startDir) is null;

    /// <summary>Rejects flag combinations that cannot mean anything, each naming its own flags.</summary>
    /// <remarks>
    /// One error per problem, never a combined "invalid mode" message: an agent needs to know which flag to
    /// change, and a single catch-all tells it nothing.
    /// </remarks>
    internal static string? ValidateFlags(bool pullRequested, string? settingsFile, bool standalone, string? solutionName, bool projectFound)
    {
        if (pullRequested && !string.IsNullOrWhiteSpace(settingsFile))
            return "--pull and --settings-file can't be used together: --settings-file names a file to apply, and --pull writes one.";

        if (!standalone && !string.IsNullOrWhiteSpace(solutionName) && projectFound)
            return "--solution-name only applies outside a Flowline project. Inside one the solution comes from the project — remove the flag.";

        return null;
    }

    /// <summary>The message a role keyword earns when there is no project to resolve it from.</summary>
    internal static string BuildStandaloneRoleError(string target) =>
        $"'{target}' is a role name, and roles resolve from .flowline — there's no Flowline project here. " +
        "Pass the environment URL instead.";

    /// <summary>Names the environment and file a run resolved, before anything is written.</summary>
    /// <remarks>
    /// The only wrong-file-wrong-environment guard on an unattended run: there is no confirmation (KTD10),
    /// and the all-skipped Inconclusive exit only fires when nothing matched at all.
    /// </remarks>
    internal static string BuildResolutionNote(string environment, SettingsFileLocation location) =>
        $"Applying [bold]{Markup.Escape(Path.GetFileName(location.Path))}[/] " +
        $"({SourceLabel(location.Source)}) to [bold]{Markup.Escape(environment)}[/]";

    static string SourceLabel(SettingsFileSource source) => source switch
    {
        SettingsFileSource.Explicit => "--settings-file",
        SettingsFileSource.RoleConvention => "role convention",
        _ => "shared fallback",
    };

    /// <summary>Dry-run completion wording, in the statement form the other commands use.</summary>
    internal static string BuildDryRunCompleteMessage(string environment) =>
        $"Dry run complete — {Markup.Escape(environment)} is untouched. Run without --dry-run to apply.";

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var standalone = IsStandalone(settings);
        var projectFound = FindFlowlineProjectRoot(Directory.GetCurrentDirectory()) is not null;
        var pullRequested = settings.Pull.IsSet;

        var flagError = ValidateFlags(pullRequested, settings.SettingsFile, standalone, settings.SolutionName, projectFound);
        if (flagError is not null)
            throw new FlowlineException(ExitCode.ValidationFailed, flagError);

        if (pullRequested)
            throw new FlowlineException(ExitCode.ValidationFailed,
                "--pull isn't implemented yet. Apply an existing settings file, or create one with pac solution create-settings.");

        // A role keyword has nothing to resolve against outside a project: Config is a bare ProjectConfig
        // there, so every role would fall through to a config-shaped "URL is required" pointing at a
        // .flowline that was never expected to exist.
        var role = DriftCommand.TryResolveRole(settings.Target);
        if (standalone && role is not null)
            throw new FlowlineException(ExitCode.ConfigInvalid, BuildStandaloneRoleError(settings.Target));

        var (env, profile) = await ResolveEnvironmentAsync(settings.Target, role, settings, cancellationToken);

        var solutionName = standalone
            ? settings.SolutionName!
            : (await GetAndCheckSolutionAsync(null, env.EnvironmentUrl!, includeManaged: null, settings, cancellationToken)).projectSolution.UniqueName;

        var location = await ResolveSettingsFileAsync(settings, role, standalone, cancellationToken);
        if (!location.Exists)
            throw new FlowlineException(ExitCode.NotFound,
                $"No settings file at '{ConsolePath.FormatRelativePath(location.Path, RootFolder)}'. " +
                "Create one with pac solution create-settings, or pass --settings-file.");

        Console.Info(BuildResolutionNote(env.DisplayName ?? settings.Target, location));

        var document = SettingsFileReader.Read(location.Path);
        var mode = settings.DryRun ? RunMode.DryRun : RunMode.Normal;

        var (service, _) = await ConnectToDataverseAsync(dataverseConnector, env.EnvironmentUrl!, cancellationToken, profile);

        var inventory = await Console.Status().FlowlineSpinner().StartAsync(
            $"Reading [bold]{Markup.Escape(solutionName)}[/] components...",
            _ => SolutionComponentInventory.ReadAsync(service, solutionName, cancellationToken));

        var outcome = await new ConfigureApplyService()
            .ApplyAsync(service, document, inventory, mode, cancellationToken);

        Report(outcome, mode, env.DisplayName ?? settings.Target);

        return (int)outcome.ExitCode;
    }

    /// <summary>
    /// Resolves a role through the shared role path and anything else as a URL.
    /// </summary>
    /// <remarks>
    /// The role branch keeps its environment-type guard (R5c) — that check is what catches a stale role URL
    /// in <c>.flowline</c> pointed at the wrong environment, and inheriting it costs nothing. It updates the
    /// config in memory only; nothing here saves, so a dry run leaves the file untouched.
    ///
    /// The URL branch has no type guard on purpose: a URL names the environment outright, so there is no
    /// role to disagree with, and the base standalone helper would refuse Production outright.
    /// </remarks>
    async Task<(EnvironmentInfo Info, PacProfile Profile)> ResolveEnvironmentAsync(
        string target, EnvironmentRole? role, Settings settings, CancellationToken ct)
    {
        if (role is not null)
            return await GetAndCheckEnvironmentInfoAsync(role.Value, null, settings, ct);

        var profile = await ProfileResolutionService.ResolveAsync(target, ct);
        var env = await Console.Status().FlowlineSpinner().StartAsync(
            $"Checking [bold]{Markup.Escape(target)}[/]...",
            _ => FlowlineValidator.Default.GetEnvironmentInfoByUrlAsync(target, profile, settings, ct));

        if (env is null)
            throw new FlowlineException(ExitCode.ConnectionFailed,
                $"Environment not found — check the URL '{target}' or your PAC login.");

        Console.Ok($"Env [bold]{Markup.Escape(env.DisplayName ?? target)}[/] ({env.EnvironmentUrl}) exists");
        return (env, profile);
    }

    /// <summary>
    /// Decides which folder the convention is anchored to, then locates the file in it.
    /// </summary>
    /// <remarks>
    /// The anchor is the Dataverse solution folder — resolved from the solution file, never composed —
    /// because that is where <c>pac solution create-settings</c> output naturally lands, beside the
    /// <c>.cdsproj</c>. An explicit path or stand-alone mode has no project to resolve, so it anchors on the
    /// working directory instead.
    /// </remarks>
    async Task<SettingsFileLocation> ResolveSettingsFileAsync(
        Settings settings, EnvironmentRole? role, bool standalone, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(settings.SettingsFile) || standalone)
            return SettingsFileLocator.Locate(Directory.GetCurrentDirectory(), role?.ToString(), settings.SettingsFile);

        var layout = await SolutionFileLayout.LoadAsync(RootFolder, ct);
        return SettingsFileLocator.Locate(layout.DataverseSolutionFolder, role?.ToString(), settings.SettingsFile);
    }

    /// <summary>
    /// Reports every component on its own line, then the one summary line (R11a).
    /// </summary>
    /// <remarks>
    /// Values never appear (R10a); names and states always do. A run whose only signal is an exit code leaves
    /// an operator, or an agent, nothing to act on.
    /// </remarks>
    void Report(ApplyOutcome outcome, RunMode mode, string environment)
    {
        foreach (var component in outcome.Components)
        {
            var name = Markup.Escape(component.Name);
            switch (component.Outcome)
            {
                case ComponentOutcomeKind.Applied when mode.IsReportOnly():
                    Console.Info($"Would change [bold]{name}[/]");
                    break;
                case ComponentOutcomeKind.Applied:
                    Console.Ok(component.WasSuspended
                        ? $"[bold]{name}[/] activated — it was Suspended, and the platform may suspend it again"
                        : $"[bold]{name}[/] set");
                    break;
                case ComponentOutcomeKind.Unchanged:
                    Console.Verbose($"{name} already matches");
                    break;
                case ComponentOutcomeKind.Skipped:
                    Console.Skip(Markup.Escape(component.Detail ?? $"{component.Name} skipped"));
                    break;
                case ComponentOutcomeKind.Failed:
                    Console.Error(Markup.Escape(component.Detail ?? $"{component.Name} failed"));
                    break;
            }
        }

        foreach (var undeclared in outcome.Undeclared)
            Console.Verbose($"Not declared in the settings file: {Markup.Escape(undeclared)}");

        if (outcome.Undeclared.Count > 0)
            Console.Warning($"{outcome.Undeclared.Count} solution component(s) aren't declared in this file — run with --verbose to list them.");

        Console.Info(outcome.SummaryLine());

        if (mode.IsReportOnly())
            Console.Done(BuildDryRunCompleteMessage(environment));
    }
}
