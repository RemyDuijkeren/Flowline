using System.ComponentModel;
using Flowline.Core;
using Flowline.Core.Configure;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Infrastructure;
using Flowline.Services;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

/// <summary>The environment and the optional component name every kind operation takes (R7).</summary>
public abstract class SettingsComponentSettings : SettingsSettings
{
    [CommandArgument(0, "<target>")]
    [Description("Target environment: prod, uat, test, dev, or a URL")]
    public string Target { get; set; } = null!;

    [CommandArgument(1, "[name]")]
    [Description("Component to address. Omit to pick one")]
    public string? Name { get; set; }
}

/// <summary>
/// What the five component operations share: everything except which flags they take (KTD4).
/// </summary>
/// <remarks>
/// Two command classes rather than one with a kind positional, because the parser then rejects an unknown
/// kind for free and the help lists the kinds. Two rather than five, because the only thing that actually
/// differs between the three state kinds and the two value kinds is the flag set — the kind itself is data,
/// read from the invoked name.
/// </remarks>
public abstract class SettingsComponentCommandBase<TSettings>(
    IAnsiConsole console,
    DataverseConnector dataverseConnector,
    FlowlineRuntimeOptions runtimeOptions,
    ProfileResolutionService profileResolutionService,
    ILoggerFactory loggerFactory,
    SubprocessCapture capture,
    NuGetVersionClient nuGetVersionClient)
    : SettingsCommandBase<TSettings>(console, dataverseConnector, runtimeOptions, profileResolutionService, loggerFactory, capture, nuGetVersionClient)
    where TSettings : SettingsComponentSettings
{
    /// <summary>Which component kind the invoked operation name means.</summary>
    protected abstract ConfigurableComponentKind KindOf(string operation);

    /// <summary>Rejects a flag pair that cannot mean anything, before anything is read or written.</summary>
    protected virtual string? ValidateOperationFlags(TSettings settings) => null;

    /// <summary>Reads or writes the resolved component.</summary>
    protected abstract Task<SingleComponentOutcome> RunAsync(
        IOrganizationServiceAsync2 service, SolutionInventory inventory, ConfigurableComponentKind kind,
        string name, TSettings settings, RunMode mode, CancellationToken ct);

    /// <summary>Whether this invocation writes, as opposed to reading the current state or value.</summary>
    protected abstract bool IsWrite(TSettings settings);

    protected override bool IsStandalone(TSettings settings) =>
        SettingsSupport.ResolveComponentStandalone(Directory.GetCurrentDirectory());

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, TSettings settings, CancellationToken cancellationToken)
    {
        var kind = KindOf(context.Name);

        var operationFlagError = ValidateOperationFlags(settings);
        if (operationFlagError is not null)
            throw new FlowlineException(ExitCode.ValidationFailed, operationFlagError);

        var (standalone, _) = ResolveProjectMode(settings);

        // Without a solution there is no inventory to address a name against, so this fails here rather
        // than later with nothing to read.
        if (standalone && string.IsNullOrWhiteSpace(settings.SolutionName))
            throw new FlowlineException(ExitCode.ConfigInvalid,
                "Couldn't tell which solution this is. Pass --solution-name.");

        var role = ResolveRoleOrThrow(settings.Target, standalone);

        var (env, profile) = await ResolveEnvironmentAsync(settings.Target, role, settings, cancellationToken);
        var solutionName = await ResolveSolutionNameAsync(settings, standalone, null, env, cancellationToken);

        var mode = settings.DryRun ? RunMode.DryRun : RunMode.Normal;
        var (service, _) = await ConnectToDataverseAsync(DataverseConnector, env.EnvironmentUrl!, cancellationToken, profile);

        var inventory = await ReadInventoryAsync(service, solutionName, cancellationToken);

        var name = settings.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            // R14: nothing of this kind in the target is a statement, not a failure. There is no name the
            // caller could have supplied instead, so there is nothing to send them back to fix.
            var candidates = inventory.OfKind(kind).ToArray();
            if (candidates.Length == 0)
            {
                Console.Info($"No {SettingsComponentNames.Plural(kind)} in this solution.");
                return (int)ExitCode.Success;
            }

            name = await ResolveMissingNameAsync(kind, candidates, cancellationToken);
        }

        var outcome = await RunAsync(service, inventory, kind, name, settings, mode, cancellationToken);

        var isWrite = IsWrite(settings);

        Report(outcome, kind, mode);

        // Before the finish line, not after it: the finish line is documented as always last, and a
        // warning printed under it reads as belonging to the next command.
        //
        // Unchanged counts. A component already in the state the file disagrees with is exactly when the
        // next push moves it, and skipping the warning there hid the case most worth warning about — the
        // operator sees "already matches" and concludes there is nothing to reconcile.
        if (isWrite && outcome.Action is SingleComponentActionKind.Applied or SingleComponentActionKind.Unchanged)
            await WarnIfTheFileWouldOverrideAsync(kind, outcome, settings, role, standalone, cancellationToken);

        if (isWrite)
            Console.Done(mode.IsReportOnly()
                ? "Dry run complete — nothing was written. Run without --dry-run to apply."
                : "Done. The settings file still decides this on the next push.");

        return (int)SettingsComponentOutcomes.ExitCodeFor(outcome);
    }

    /// <summary>
    /// What to do when no component name was given (R9).
    /// </summary>
    /// <remarks>
    /// The non-interactive half lives here. An unattended caller gets the names it could have passed and a
    /// typed failure naming the argument — never a prompt it cannot answer, which would hang the run.
    /// </remarks>
    protected virtual async Task<string> ResolveMissingNameAsync(
        ConfigurableComponentKind kind, IReadOnlyList<InventoryComponent> candidates, CancellationToken ct)
    {
        var ordered = candidates.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToArray();

        // The capability check comes before the prompt is built, not around showing it — the order every
        // other prompt in this codebase uses. An unattended caller must never reach a prompt it cannot
        // answer, because that hangs the run rather than failing it.
        if (!IsInteractive())
        {
            foreach (var candidate in ordered)
                Console.WriteLine(candidate.Name);

            throw new FlowlineException(ExitCode.ValidationFailed,
                $"Name which {SettingsComponentNames.Singular(kind)} to address — the ones in this solution are listed above.");
        }

        // Single-select with search rather than a multi-select: exactly one component changes per
        // invocation, and typing part of a name is what makes a long solution navigable. The inventory
        // spinner has already closed by the time this runs — Spectre forbids a prompt inside a status
        // display (KTD10).
        var prompt = new SelectionPrompt<InventoryComponent>()
            .Title(FlowlineConsoleExtensions.Question($"Pick a {SettingsComponentNames.Singular(kind)}:"))
            .UseConverter(c => SettingsComponentOutcomes.DescribeCandidate(c, kind))
            .EnableSearch()
            .AddChoices(ordered);

        return (await Console.PromptAsync(prompt, ct)).Name;
    }


    /// <summary>
    /// Warns that the next push will put this component back the way the file says (R4, R15, KTD12).
    /// </summary>
    /// <remarks>
    /// The test is whether a push would actually act on the component, not merely whether the file names
    /// it: the apply path skips an entry with an empty value, so warning about one would train an operator
    /// to ignore the warning.
    ///
    /// Stand-alone mode says nothing at all rather than reporting no match. Locating the file needs the
    /// project layout, which stand-alone mode does not have, so silence there would otherwise read as
    /// "no file names it" — which is a different, and unearned, claim.
    /// </remarks>
    async Task WarnIfTheFileWouldOverrideAsync(
        ConfigurableComponentKind kind, SingleComponentOutcome outcome, TSettings settings,
        EnvironmentRole? role, bool standalone, CancellationToken ct)
    {
        if (standalone) return;

        var location = await ResolveSettingsFileAsync(null, role, standalone: false, null, forWriting: false, ct);
        if (!location.Exists) return;

        var (writtenEnabled, writtenValue) = WrittenBy(settings);

        if (!ConfigureApplyService.WouldOverride(
                SettingsFileReader.Read(location.Path), kind, outcome.Component.Name, writtenEnabled, writtenValue))
            return;

        Console.Warning(
            $"{Markup.Escape(Path.GetFileName(location.Path))} declares {Markup.Escape(outcome.Component.Name)} " +
            "differently — the next 'settings push' will put it back. Change the file too to make this stick.");
    }

    /// <summary>What this invocation asked to write, for the override comparison.</summary>
    protected abstract (bool? Enabled, string? Value) WrittenBy(TSettings settings);

    void Report(SingleComponentOutcome outcome, ConfigurableComponentKind kind, RunMode mode)
    {
        var name = Markup.Escape(outcome.Component.Name);

        switch (outcome.Action)
        {
            case SingleComponentActionKind.Read:
                Console.Info($"[bold]{name}[/] is {Markup.Escape(SettingsComponentOutcomes.DescribeCurrent(outcome, kind))}");
                break;
            case SingleComponentActionKind.Applied when mode.IsReportOnly():
                Console.Info(SettingsSupport.BuildWouldChangeLine(outcome.Component.Name));
                break;
            case SingleComponentActionKind.Applied:
                Console.Ok(SettingsSupport.BuildUpdatedLine(outcome.Component.Name, outcome.WasSuspended));
                break;
            case SingleComponentActionKind.Unchanged:
                Console.Skip($"{name} already matches — nothing written");
                break;
            case SingleComponentActionKind.Skipped:
                Console.Error(Markup.Escape(outcome.Detail ?? $"{outcome.Component.Name} was refused"));
                break;
            case SingleComponentActionKind.Failed:
                Console.Error(Markup.Escape(outcome.Detail ?? $"{outcome.Component.Name} failed"));
                break;
        }
    }

}

/// <summary>How a single-component outcome is described and scored.</summary>
internal static class SettingsComponentOutcomes
{
    /// <summary>
    /// How a read describes what it found.
    /// </summary>
    /// <remarks>
    /// A suspended flow is called suspended rather than off. Dataverse flattens it to not-enabled, and an
    /// operator who reads "off" for a flow that stopped itself will turn it on and be surprised when it
    /// stops again.
    /// </remarks>
    public static string DescribeCurrent(SingleComponentOutcome outcome, ConfigurableComponentKind kind) =>
        kind is ConfigurableComponentKind.EnvironmentVariable or ConfigurableComponentKind.ConnectionReference
            ? outcome.PriorValue is { Length: > 0 } value ? value : "unset"
            : outcome.WasSuspended ? "suspended" : outcome.PriorEnabled == true ? "on" : "off";

    /// <summary>How a component reads in the picker: its addressable name, then what it currently is.</summary>
    /// <remarks>
    /// The name comes first because it is what the caller would otherwise have typed, and what the search
    /// filters on.
    ///
    /// A value kind shows its name alone. An environment variable's value is not on the inventory row at
    /// all, and a connection reference's is a connection id nobody recognizes — and filling the label with
    /// values would print a Dataverse-stored secret into the picker, which is the exposure the read path
    /// already accepts and this one has no reason to add to.
    ///
    /// The name is escaped because Spectre parses a selection prompt's converter output as markup, and a
    /// component name is whatever someone typed in the maker portal. A flow called "[Account] nightly
    /// sync" crashed the picker with "Could not find color or style 'Account'" — square brackets are
    /// ordinary in a flow name and a style tag to the renderer.
    /// </remarks>
    public static string DescribeCandidate(InventoryComponent component, ConfigurableComponentKind kind)
    {
        var name = Markup.Escape(component.Name);

        return kind is ConfigurableComponentKind.EnvironmentVariable or ConfigurableComponentKind.ConnectionReference
            ? name
            : $"{name} — {(component.Suspended ? "suspended" : component.Enabled == true ? "on" : "off")}";
    }

    /// <summary>
    /// The typed code a single-component outcome earns.
    /// </summary>
    /// <remarks>
    /// A refusal is <see cref="ExitCode.ValidationFailed"/>: the caller asked for something the command
    /// will not do — an empty value, a pull placeholder, a secret it cannot read — and the fix is to change
    /// the invocation.
    ///
    /// A failed write is <see cref="ExitCode.PartialSuccess"/>, matching the file-apply path so 18 means
    /// the same thing in both. That code's contract already covers an all-failed run, which for one
    /// component is what this is, and the recovery is identical: fix the cause and re-run.
    ///
    /// <see cref="ExitCode.Inconclusive"/> deliberately never appears here (KTD8). It means a run compared
    /// nothing, which cannot happen when a name resolved to exactly one component.
    /// </remarks>
    public static ExitCode ExitCodeFor(SingleComponentOutcome outcome) => outcome.Action switch
    {
        SingleComponentActionKind.Skipped => ExitCode.ValidationFailed,
        SingleComponentActionKind.Failed => ExitCode.PartialSuccess,
        _ => ExitCode.Success,
    };
}

/// <summary>What each kind is called in a sentence.</summary>
internal static class SettingsComponentNames
{
    public static string Singular(ConfigurableComponentKind kind) => kind switch
    {
        ConfigurableComponentKind.CloudFlow => "flow",
        ConfigurableComponentKind.Workflow => "workflow",
        ConfigurableComponentKind.PluginStep => "plugin step",
        ConfigurableComponentKind.EnvironmentVariable => "environment variable",
        ConfigurableComponentKind.ConnectionReference => "connection reference",
        _ => "component",
    };

    public static string Plural(ConfigurableComponentKind kind) => kind switch
    {
        ConfigurableComponentKind.CloudFlow => "cloud flows",
        ConfigurableComponentKind.Workflow => "classic workflows",
        ConfigurableComponentKind.PluginStep => "plugin steps",
        ConfigurableComponentKind.EnvironmentVariable => "environment variables",
        ConfigurableComponentKind.ConnectionReference => "connection references",
        _ => "components",
    };
}

/// <summary>Turns a cloud flow, classic workflow or plugin step on or off, or reads its state.</summary>
public class SettingsStateCommand(
    IAnsiConsole console,
    DataverseConnector dataverseConnector,
    FlowlineRuntimeOptions runtimeOptions,
    ProfileResolutionService profileResolutionService,
    ILoggerFactory loggerFactory,
    SubprocessCapture capture,
    NuGetVersionClient nuGetVersionClient)
    : SettingsComponentCommandBase<SettingsStateCommand.Settings>(console, dataverseConnector, runtimeOptions, profileResolutionService, loggerFactory, capture, nuGetVersionClient)
{
    public sealed class Settings : SettingsComponentSettings
    {
        [CommandOption("--on")]
        [Description("Turn it on")]
        [DefaultValue(false)]
        public bool On { get; set; } = false;

        [CommandOption("--off")]
        [Description("Turn it off")]
        [DefaultValue(false)]
        public bool Off { get; set; } = false;
    }

    // The kind is the operation, so the parser rejects --value on a flow with no hand-written check. This
    // pair is the only contradiction left to check (KTD5).
    protected override string? ValidateOperationFlags(Settings settings) =>
        ValidateStateFlags(settings.On, settings.Off);

    internal static string? ValidateStateFlags(bool on, bool off) =>
        on && off
            ? "--on and --off can't both be given: pick the state you want."
            : null;

    protected override ConfigurableComponentKind KindOf(string operation) => KindFor(operation);

    internal static ConfigurableComponentKind KindFor(string operation) => operation switch
    {
        "flow" => ConfigurableComponentKind.CloudFlow,
        "workflow" => ConfigurableComponentKind.Workflow,
        "plugin" => ConfigurableComponentKind.PluginStep,
        // Unreachable: the parser only routes the three names registered for this class. An exception
        // rather than a typed exit code, because reaching it would be a registration bug, not user input.
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Not a state operation."),
    };

    protected override bool IsWrite(Settings settings) => settings.On || settings.Off;

    protected override (bool? Enabled, string? Value) WrittenBy(Settings settings) =>
        (IsWrite(settings) ? settings.On : null, null);

    protected override Task<SingleComponentOutcome> RunAsync(
        IOrganizationServiceAsync2 service, SolutionInventory inventory, ConfigurableComponentKind kind,
        string name, Settings settings, RunMode mode, CancellationToken ct) =>
        SingleComponentService.ReadOrWriteStateAsync(
            service, inventory, kind, name,
            desiredEnabled: IsWrite(settings) ? settings.On : null,
            mode, ct);
}

/// <summary>Sets an environment variable's value or binds a connection reference, or reads it.</summary>
public class SettingsValueCommand(
    IAnsiConsole console,
    DataverseConnector dataverseConnector,
    FlowlineRuntimeOptions runtimeOptions,
    ProfileResolutionService profileResolutionService,
    ILoggerFactory loggerFactory,
    SubprocessCapture capture,
    NuGetVersionClient nuGetVersionClient)
    : SettingsComponentCommandBase<SettingsValueCommand.Settings>(console, dataverseConnector, runtimeOptions, profileResolutionService, loggerFactory, capture, nuGetVersionClient)
{
    public sealed class Settings : SettingsComponentSettings
    {
        [CommandOption("--value <value>")]
        [Description("The value to set, or the connection id to bind. Omit to read the current one")]
        public string? Value { get; set; }
    }

    protected override ConfigurableComponentKind KindOf(string operation) => KindFor(operation);

    internal static ConfigurableComponentKind KindFor(string operation) => operation switch
    {
        "envvar" => ConfigurableComponentKind.EnvironmentVariable,
        "connref" => ConfigurableComponentKind.ConnectionReference,
        // Unreachable, for the same reason as the state operations above.
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Not a value operation."),
    };

    protected override bool IsWrite(Settings settings) => settings.Value is not null;

    protected override (bool? Enabled, string? Value) WrittenBy(Settings settings) => (null, settings.Value);

    protected override Task<SingleComponentOutcome> RunAsync(
        IOrganizationServiceAsync2 service, SolutionInventory inventory, ConfigurableComponentKind kind,
        string name, Settings settings, RunMode mode, CancellationToken ct) =>
        SingleComponentService.ReadOrWriteValueAsync(service, inventory, kind, name, settings.Value, mode, ct);
}
