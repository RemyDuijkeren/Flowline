using System.ComponentModel;
using Flowline.Core;
using Flowline.Core.Configure;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Infrastructure;
using Flowline.Services;
using Flowline.Utils;
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

    /// <summary>
    /// Runs the read or write behind a spinner, so the run says what it is waiting on.
    /// </summary>
    /// <remarks>
    /// Turning a flow on is one Dataverse call that can take several seconds, and without this the CLI
    /// printed the current state and then sat silent long enough to look hung. Indeterminate rather than a
    /// progress bar: it is a single call with nothing to count.
    ///
    /// A prompt cannot run inside a status display, so this deliberately wraps the call alone (KTD10) —
    /// the spinner opens after an answer and closes before the next question.
    /// </remarks>
    Task<SingleComponentOutcome> RunWithSpinnerAsync(
        IOrganizationServiceAsync2 service, SolutionInventory inventory, ConfigurableComponentKind kind,
        string name, TSettings settings, RunMode mode, bool isWrite, CancellationToken ct)
    {
        // A dry run is checking, not updating: it runs the same comparison and stops before the write.
        var verb = !isWrite || mode.IsReportOnly() ? "Reading" : "Updating";

        return Console.Status().FlowlineSpinner().StartAsync(
            $"{verb} [bold]{Markup.Escape(name)}[/]...",
            _ => RunAsync(service, inventory, kind, name, settings, mode, ct));
    }

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

            // No name in an unattended run is an inventory question, not a malformed invocation: the
            // answer is the list, and a run that prints what was asked for succeeded.
            if (!IsInteractive())
            {
                ListCandidates(kind, candidates);
                return (int)ExitCode.Success;
            }

            name = await PickAsync(kind, candidates, cancellationToken);
        }

        var isWrite = IsWrite(settings);

        var outcome = await RunWithSpinnerAsync(
            service, inventory, kind, name, settings, mode, isWrite, cancellationToken);

        // A read at a terminal is someone deciding, not someone reporting. Show what is there, then ask
        // what it should be — the picker otherwise ends by printing a state the operator just looked at
        // and offering no way to change it.
        if (!isWrite && IsInteractive())
        {
            Report(outcome, kind, mode);

            if (!await PromptForTargetAsync(kind, outcome, settings, env, cancellationToken))
                return (int)ExitCode.Success;

            outcome = await RunWithSpinnerAsync(
                service, inventory, kind, outcome.Component.Name, settings, mode, isWrite: true,
                cancellationToken);
            isWrite = true;
        }

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
    /// Answers a no-name unattended run with the inventory it was asking about (R9).
    /// </summary>
    /// <remarks>
    /// Sorted case-insensitively so a caller copying a name out of a long list can find it, and written
    /// as plain text rather than markup — a component name is whatever someone typed in the maker portal,
    /// and square brackets are ordinary in one.
    ///
    /// A value kind lists names alone. Showing every environment variable value would turn one read's
    /// accepted exposure into a whole solution's worth, in the runs most likely to be piped or logged.
    /// The value is still there for the asking, one named read at a time.
    /// </remarks>
    protected void ListCandidates(ConfigurableComponentKind kind, IReadOnlyList<InventoryComponent> candidates)
    {
        foreach (var candidate in SettingsComponentOutcomes.Ordered(candidates, kind))
            Console.WriteLine(SettingsComponentOutcomes.Describe(candidate, kind));
    }

    /// <summary>Asks which component to address, when the run is at a terminal (R9).</summary>
    /// <remarks>
    /// Single-select with search rather than a multi-select: exactly one component changes per invocation,
    /// and typing part of a name is what makes a long solution navigable. The inventory spinner has already
    /// closed by the time this runs — Spectre forbids a prompt inside a status display (KTD10).
    /// </remarks>
    protected virtual async Task<string> PickAsync(
        ConfigurableComponentKind kind, IReadOnlyList<InventoryComponent> candidates, CancellationToken ct)
    {
        var prompt = new SelectionPrompt<InventoryComponent>()
            .Title(FlowlineConsoleExtensions.Question($"Pick a {SettingsComponentNames.Singular(kind)}:"))
            .UseConverter(c => SettingsComponentOutcomes.DescribeCandidate(c, kind))
            .EnableSearch()
            .AddChoices(SettingsComponentOutcomes.Ordered(candidates, kind));

        return (await Console.PromptAsync(prompt, ct)).Name;
    }

    /// <summary>
    /// Asks what the state or value should now be, having just shown what it is (R9).
    /// </summary>
    /// <remarks>
    /// Records the answer on <paramref name="settings"/> rather than returning it, so the flags stay the
    /// one place that says what an invocation asked for. A second route into the writers would be free to
    /// disagree with them.
    /// </remarks>
    /// <returns><c>true</c> when the answer asked for a write.</returns>
    protected abstract Task<bool> PromptForTargetAsync(
        ConfigurableComponentKind kind, SingleComponentOutcome current, TSettings settings,
        EnvironmentInfo environment, CancellationToken ct);


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

        // The write has already happened by the time this runs, so a file this cannot read is a reason to
        // say less, not to fail. Reporting the component's write as a failed run would send someone to
        // undo a change that actually succeeded.
        SettingsDocument document;
        try
        {
            document = SettingsFileReader.Read(location.Path);
        }
        catch (FlowlineException ex)
        {
            Console.Warning(
                $"Couldn't check {Markup.Escape(Path.GetFileName(location.Path))} — {Markup.Escape(ex.Message)} " +
                "The change was made; whether the next 'settings push' puts it back is unknown.");
            return;
        }

        var (writtenEnabled, writtenValue) = WrittenBy(settings);

        if (!ConfigureApplyService.WouldOverride(
                document, kind, outcome.Component.Name, writtenEnabled, writtenValue))
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

    /// <summary>
    /// The order a list of candidates is offered in: the ones worth acting on first (R9c).
    /// </summary>
    /// <remarks>
    /// Suspended, then off, then on. A component that stopped itself is the one nobody meant, a component
    /// that is off is usually why someone opened the list, and the ones already on are the ones they
    /// scroll past. Name order within each group, so a long solution stays navigable.
    ///
    /// Value kinds have no state, so they keep plain name order.
    /// </remarks>
    public static InventoryComponent[] Ordered(
        IReadOnlyList<InventoryComponent> candidates, ConfigurableComponentKind kind) =>
        IsValueKind(kind)
            ? candidates.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToArray()
            : candidates
                .OrderBy(c => c.Suspended ? 0 : c.Enabled == true ? 2 : 1)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

    /// <summary>How a component reads in a list: what it is, then its addressable name.</summary>
    /// <remarks>
    /// <b>State first, because it is the only position a terminal cannot take away.</b> A plugin step is
    /// named for its class and message and runs past a hundred characters, so a trailing state wrapped
    /// onto its own line and stopped lining up. At column zero it is read before the wrap happens.
    ///
    /// <b>A shape and a word, not one or the other.</b> The glyph is what the eye scans down; the word is
    /// what the picker's search matches, so typing "off" narrows a long list to the ones that are. A
    /// symbol alone would lose the filter and a word alone would lose the column.
    ///
    /// <b>Suspended gets its own shape.</b> Dataverse flattens it to not-enabled, and an operator who
    /// reads a stopped-itself flow as off turns it on and is surprised when it stops again. A half-filled
    /// circle says that better than a second hollow one.
    ///
    /// <b>On and off are padded to each other; suspended is not.</b> Padding every row out to the width
    /// of "suspended" would cost twelve columns on rows that already overflow, to align a state that is
    /// rare. A ragged suspended row is the cheaper trade.
    ///
    /// A value kind shows its name alone. An environment variable's value is not on the inventory row at
    /// all, a connection reference's is an id nobody recognises, and filling the label with values would
    /// print a Dataverse-stored secret into the list.
    /// </remarks>
    public static string Describe(InventoryComponent component, ConfigurableComponentKind kind)
    {
        if (IsValueKind(kind)) return component.Name;

        var (glyph, word, _) = StateOf(component);

        return $"{glyph} {word}  {component.Name}";
    }

    /// <summary>
    /// The same line for the picker, coloured and safe to render as markup.
    /// </summary>
    /// <remarks>
    /// Colour reinforces the shape rather than carrying it, so the state still reads for anyone who does
    /// not separate red from green. Observed against a real terminal: the selection highlight recolours
    /// the name but leaves the state's colour alone, so the highlighted row keeps its signal.
    ///
    /// The name is escaped because Spectre parses a selection prompt's converter output as markup, and a
    /// component name is whatever someone typed in the maker portal. A flow called "[Account] nightly
    /// sync" crashed the picker with "Could not find color or style 'Account'": square brackets are
    /// ordinary in a flow name and a style tag to the renderer.
    /// </remarks>
    public static string DescribeCandidate(InventoryComponent component, ConfigurableComponentKind kind)
    {
        var name = Markup.Escape(component.Name);

        if (IsValueKind(kind)) return name;

        var (glyph, word, colour) = StateOf(component);

        return $"[{colour}]{glyph} {word}[/]  {name}";
    }

    /// <summary>The glyph, word and colour one component's state reads as.</summary>
    static (string Glyph, string Word, string Colour) StateOf(InventoryComponent component) =>
        component.Suspended ? (SuspendedGlyph, "suspended", "yellow")
        : component.Enabled == true ? (OnGlyph, Pad("on"), "green")
        : (OffGlyph, Pad("off"), "red");

    // Wide enough for "off", which pads "on" to match it and leaves "suspended" alone.
    static string Pad(string word) => word.PadRight(3);

    const string OnGlyph = "\u25cf";        // ● filled: running
    const string SuspendedGlyph = "\u25d0"; // ◐ half: it stopped itself
    const string OffGlyph = "\u25cb";       // ○ hollow: not running

    static bool IsValueKind(ConfigurableComponentKind kind) =>
        kind is ConfigurableComponentKind.EnvironmentVariable or ConfigurableComponentKind.ConnectionReference;

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

    const string LeaveIt = "leave it as it is";

    protected override async Task<bool> PromptForTargetAsync(
        ConfigurableComponentKind kind, SingleComponentOutcome current, Settings settings,
        EnvironmentInfo environment, CancellationToken ct)
    {
        // The opposite of what it is goes first, so the common answer is one Enter away: someone who read
        // the state and stayed for the prompt is nearly always there to flip it. Suspended counts as on —
        // the useful next move on a flow that stopped itself is to start it, and Dataverse offers no way
        // to ask for suspended.
        var flip = current.PriorEnabled == true || current.WasSuspended ? "off" : "on";

        var answer = await Console.PromptAsync(
            new SelectionPrompt<string>()
                .Title(FlowlineConsoleExtensions.Question($"Set {Markup.Escape(current.Component.Name)} to:"))
                .AddChoices(flip, flip == "on" ? "off" : "on", LeaveIt), ct);

        if (answer == LeaveIt) return false;

        settings.On = answer == "on";
        settings.Off = !settings.On;
        return true;
    }

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

    protected override Task<bool> PromptForTargetAsync(
        ConfigurableComponentKind kind, SingleComponentOutcome current, Settings settings,
        EnvironmentInfo environment, CancellationToken ct) =>
        kind == ConfigurableComponentKind.ConnectionReference
            ? PromptForConnectionAsync(current, settings, environment, ct)
            : PromptForTypedValueAsync("value", settings, ct);

    /// <summary>Asks for a value outright, for the kind whose values are not enumerable.</summary>
    /// <remarks>
    /// Blank means leave it, not clear it. Clearing a value is not supported, and an empty answer is what
    /// someone types to back out of a prompt they did not mean to reach.
    /// </remarks>
    async Task<bool> PromptForTypedValueAsync(string noun, Settings settings, CancellationToken ct)
    {
        var answer = await Console.PromptAsync(
            new TextPrompt<string>(FlowlineConsoleExtensions.Question($"New {noun} (blank to leave it):"))
                .AllowEmpty(), ct);

        if (string.IsNullOrEmpty(answer)) return false;

        settings.Value = answer;
        return true;
    }

    /// <summary>
    /// Offers the environment's connections for this reference's connector, rather than a typed id.
    /// </summary>
    /// <remarks>
    /// A connection id is a generated string nobody can produce from memory, and the only place it was
    /// previously readable was the maker portal — which is the round trip this command exists to remove.
    ///
    /// <b>Only the caller's own connections are listed.</b> Connections belong to the person who made
    /// them, so a reference bound to a colleague's connection will not appear here. That is why typing an
    /// id by hand stays on the menu rather than being replaced.
    ///
    /// <b>A failed listing narrows the menu, it does not end the run.</b> This runs after a read and
    /// before a write, on a command whose whole job is to set this one value; `pac` being unreachable is a
    /// reason to ask for the id instead of offering a list.
    /// </remarks>
    async Task<bool> PromptForConnectionAsync(
        SingleComponentOutcome current, Settings settings, EnvironmentInfo environment, CancellationToken ct)
    {
        var connectorId = current.Component.ConnectorId;

        while (true)
        {
            var connections = await Console.Status().FlowlineSpinner().StartAsync(
                "Reading this environment's connections...",
                _ => PacConnections.ListAsync(environment.EnvironmentUrl!, ct));

            var matching = PacConnections.ForConnector(connections, connectorId);

            if (matching.Count == 0)
                Console.Info("No connections here match this reference's connector, or 'pac' couldn't list them.");

            var choices = matching
                .Select(c => new ConnectionChoice($"{c.Name} ({c.Status})", ConnectionChoiceKind.Bind, c.Id))
                .Append(new ConnectionChoice("Enter a connection id by hand", ConnectionChoiceKind.Type))
                .Append(new ConnectionChoice("Create a new connection in the maker portal", ConnectionChoiceKind.Create))
                .Append(new ConnectionChoice("Leave it as it is", ConnectionChoiceKind.Leave))
                .ToArray();

            var answer = await Console.PromptAsync(
                new SelectionPrompt<ConnectionChoice>()
                    .Title(FlowlineConsoleExtensions.Question(
                        $"Bind {Markup.Escape(current.Component.Name)} to:"))
                    .UseConverter(c => Markup.Escape(c.Label))
                    .AddChoices(choices), ct);

            switch (answer.Kind)
            {
                case ConnectionChoiceKind.Leave:
                    return false;

                case ConnectionChoiceKind.Type:
                    return await PromptForTypedValueAsync("connection id", settings, ct);

                case ConnectionChoiceKind.Create:
                    OpenMakerPortalConnections(environment.EnvironmentId);

                    // Declining is the way out of the loop: someone who did not create a connection after
                    // all would otherwise have only Ctrl+C.
                    if (!await Console.PromptAsync(
                            new ConfirmationPrompt("Created it? Answer yes to list the connections again"), ct))
                        return false;

                    continue;

                default:
                    settings.Value = answer.ConnectionId;
                    return true;
            }
        }
    }

    /// <summary>Opens the environment's new-connection page in the default browser.</summary>
    /// <remarks>
    /// The portal is the only place most connections can be created: a connector that needs an interactive
    /// consent has no headless path, and `pac connection create` makes a service principal Dataverse
    /// connection and nothing else.
    ///
    /// A browser that will not open is reported rather than thrown: the URL is printed either way, and
    /// the operator can open it themselves.
    /// </remarks>
    void OpenMakerPortalConnections(Guid environmentId)
    {
        var url = $"https://make.powerapps.com/environments/{environmentId}/connections/available";

        Console.Info($"Opening {url}");

        try
        {
            using var _ = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Warning($"Couldn't open a browser ({Markup.Escape(ex.Message)}). Open that address yourself.");
        }
    }

    enum ConnectionChoiceKind { Bind, Type, Create, Leave }

    sealed record ConnectionChoice(string Label, ConnectionChoiceKind Kind, string? ConnectionId = null);

    protected override (bool? Enabled, string? Value) WrittenBy(Settings settings) => (null, settings.Value);

    protected override Task<SingleComponentOutcome> RunAsync(
        IOrganizationServiceAsync2 service, SolutionInventory inventory, ConfigurableComponentKind kind,
        string name, Settings settings, RunMode mode, CancellationToken ct) =>
        SingleComponentService.ReadOrWriteValueAsync(service, inventory, kind, name, settings.Value, mode, ct);
}
