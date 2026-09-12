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
    /// <summary>
    /// Which component classes this invocation addresses (KTD25).
    /// </summary>
    /// <remarks>
    /// One class when <c>--type</c> narrowed it, all of the command's otherwise. The kind used to come
    /// from the invoked command name, which is what made a leaf per class necessary; reading it from a
    /// flag is what lets the process family grow without the command list growing with it.
    /// </remarks>
    protected abstract IReadOnlyCollection<ConfigurableComponentKind> KindsFor(TSettings settings);

    /// <summary>Rejects a flag pair that cannot mean anything, before anything is read or written.</summary>
    protected virtual string? ValidateOperationFlags(TSettings settings) => null;

    /// <summary>Reads or writes the resolved component.</summary>
    protected abstract Task<SingleComponentOutcome> RunAsync(
        IOrganizationServiceAsync2 service, SolutionInventory inventory,
        IReadOnlyCollection<ConfigurableComponentKind> kinds,
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
        IOrganizationServiceAsync2 service, SolutionInventory inventory,
        IReadOnlyCollection<ConfigurableComponentKind> kinds,
        string name, TSettings settings, RunMode mode, bool isWrite, CancellationToken ct)
    {
        // A dry run is checking, not updating: it runs the same comparison and stops before the write.
        var verb = !isWrite || mode.IsReportOnly() ? "Reading" : "Updating";

        return Console.Status().FlowlineSpinner().StartAsync(
            $"{verb} [bold]{Markup.Escape(name)}[/]...",
            _ => RunAsync(service, inventory, kinds, name, settings, mode, ct));
    }

    protected override bool IsStandalone(TSettings settings) =>
        SettingsSupport.ResolveComponentStandalone(Directory.GetCurrentDirectory());

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, TSettings settings, CancellationToken cancellationToken)
    {
        var kinds = KindsFor(settings);

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
            var candidates = inventory.OfKinds(kinds).ToArray();
            if (candidates.Length == 0)
            {
                Console.Info($"No {SettingsComponentNames.Plural(kinds)} in this solution.");
                return (int)ExitCode.Success;
            }

            // No name in an unattended run is an inventory question, not a malformed invocation: the
            // answer is the list, and a run that prints what was asked for succeeded.
            if (!IsInteractive())
            {
                ListCandidates(kinds, candidates);
                return (int)ExitCode.Success;
            }

            // Narrowed to what was picked, not just its name. Resolution searches by name, and a name
            // can exist in two classes — a business rule and a classic workflow called "Account - set
            // name" is a real case — so carrying only the name forward threw the pick away and failed
            // as ambiguous, telling someone who had pointed at one row to go and rename something.
            var picked = await PickAsync(kinds, candidates, cancellationToken);
            name = picked.Name;
            kinds = [picked.Kind];
        }

        var isWrite = IsWrite(settings);

        var outcome = await RunWithSpinnerAsync(
            service, inventory, kinds, name, settings, mode, isWrite, cancellationToken);

        // A read at a terminal is someone deciding, not someone reporting. Show what is there, then ask
        // what it should be — the picker otherwise ends by printing a state the operator just looked at
        // and offering no way to change it.
        if (!isWrite && IsInteractive())
        {
            Report(outcome, mode);

            if (!await PromptForTargetAsync(outcome, settings, env, cancellationToken))
                return (int)ExitCode.Success;

            outcome = await RunWithSpinnerAsync(
                service, inventory, [outcome.Component.Kind], outcome.Component.Name, settings, mode,
                isWrite: true, cancellationToken);
            isWrite = true;
        }

        Report(outcome, mode);

        // Before the finish line, not after it: the finish line is documented as always last, and a
        // warning printed under it reads as belonging to the next command.
        //
        // Unchanged counts. A component already in the state the file disagrees with is exactly when the
        // next push moves it, and skipping the warning there hid the case most worth warning about — the
        // operator sees "already matches" and concludes there is nothing to reconcile.
        if (isWrite && outcome.Action is SingleComponentActionKind.Applied or SingleComponentActionKind.Unchanged)
            await WarnIfTheFileWouldOverrideAsync(outcome, settings, role, standalone, cancellationToken);

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
    protected void ListCandidates(
        IReadOnlyCollection<ConfigurableComponentKind> kinds, IReadOnlyList<InventoryComponent> candidates)
    {
        var width = SettingsComponentNames.TypeColumnWidth(kinds);

        foreach (var candidate in SettingsComponentOutcomes.Ordered(candidates))
            Console.WriteLine(SettingsComponentOutcomes.Describe(candidate, width));
    }

    /// <summary>Asks which component to address, when the run is at a terminal (R9).</summary>
    /// <remarks>
    /// Single-select with search rather than a multi-select: exactly one component changes per invocation,
    /// and typing part of a name is what makes a long solution navigable. The inventory spinner has already
    /// closed by the time this runs — Spectre forbids a prompt inside a status display (KTD10).
    /// </remarks>
    protected virtual async Task<InventoryComponent> PickAsync(
        IReadOnlyCollection<ConfigurableComponentKind> kinds, IReadOnlyList<InventoryComponent> candidates,
        CancellationToken ct)
    {
        var width = SettingsComponentNames.TypeColumnWidth(kinds);

        var prompt = new SelectionPrompt<InventoryComponent>()
            .Title(FlowlineConsoleExtensions.Question($"Pick a {SettingsComponentNames.Singular(kinds)}:"))
            .UseConverter(c => SettingsComponentOutcomes.DescribeCandidate(c, width))
            .EnableSearch()
            .AddChoices(SettingsComponentOutcomes.Ordered(candidates));

        return await Console.PromptAsync(prompt, ct);
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
        SingleComponentOutcome current, TSettings settings, EnvironmentInfo environment, CancellationToken ct);


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
        SingleComponentOutcome outcome, TSettings settings,
        EnvironmentRole? role, bool standalone, CancellationToken ct)
    {
        if (standalone) return;

        // A class the file has no section for can never be declared, so there is nothing to warn about
        // and nothing that will put it back (KTD25).
        if (!ConfigurableComponentKinds.IsFileManaged(outcome.Component.Kind)) return;

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
                document, outcome.Component.Kind, outcome.Component.Name, writtenEnabled, writtenValue))
            return;

        Console.Warning(
            $"{Markup.Escape(Path.GetFileName(location.Path))} declares {Markup.Escape(outcome.Component.Name)} " +
            "differently — the next 'settings push' will put it back. Change the file too to make this stick.");
    }

    /// <summary>What this invocation asked to write, for the override comparison.</summary>
    protected abstract (bool? Enabled, string? Value) WrittenBy(TSettings settings);

    void Report(SingleComponentOutcome outcome, RunMode mode)
    {
        var name = Markup.Escape(outcome.Component.Name);

        switch (outcome.Action)
        {
            case SingleComponentActionKind.Read:
                Console.Info($"[bold]{name}[/] is {Markup.Escape(SettingsComponentOutcomes.DescribeCurrent(outcome))}");
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
    public static string DescribeCurrent(SingleComponentOutcome outcome) =>
        IsValueKind(outcome.Component.Kind)
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
    public static InventoryComponent[] Ordered(IReadOnlyList<InventoryComponent> candidates) =>
        candidates
            // A value kind has no state, so every one of them ranks the same and the sort falls through
            // to the name. That is what a list of environment variables and connection references wants.
            .OrderBy(c => IsValueKind(c.Kind) ? 0 : c.Suspended ? 0 : c.Enabled == true ? 2 : 1)
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
    public static string Describe(InventoryComponent component, int typeWidth)
    {
        var type = SettingsComponentNames.TypeToken(component.Kind).PadRight(typeWidth);

        if (IsValueKind(component.Kind)) return $"{type}  {component.Name}";

        var (glyph, word, _) = StateOf(component);

        return $"{glyph} {word}  {type}  {component.Name}";
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
    public static string DescribeCandidate(InventoryComponent component, int typeWidth)
    {
        var name = Markup.Escape(component.Name);
        var type = SettingsComponentNames.TypeToken(component.Kind).PadRight(typeWidth);

        if (IsValueKind(component.Kind)) return $"{type}  {name}";

        var (glyph, word, colour) = StateOf(component);

        return $"[{colour}]{glyph} {word}[/]  {type}  {name}";
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

/// <summary>What each kind is called, in a sentence and in a list column.</summary>
internal static class SettingsComponentNames
{
    /// <summary>
    /// The short token a list line carries and <c>--type</c> accepts (KTD25).
    /// </summary>
    /// <remarks>
    /// One word, because it is a column on every row. It is also part of the rendered label, and the
    /// picker's search matches the label, so typing the token filters the list with no flag and no new
    /// mechanism.
    /// </remarks>
    public static string TypeToken(ConfigurableComponentKind kind) => kind switch
    {
        ConfigurableComponentKind.CloudFlow => "flow",
        ConfigurableComponentKind.Workflow => "workflow",
        ConfigurableComponentKind.BusinessRule => "rule",
        ConfigurableComponentKind.BusinessProcessFlow => "bpf",
        ConfigurableComponentKind.Action => "action",
        ConfigurableComponentKind.PluginStep => "plugin",
        ConfigurableComponentKind.EnvironmentVariable => "envvar",
        ConfigurableComponentKind.ConnectionReference => "connref",
        _ => "component",
    };

    /// <summary>How wide the type column has to be for the classes this command can show.</summary>
    /// <remarks>
    /// Measured from the kinds in play rather than from every kind there is, so narrowing with
    /// <c>--type</c> tightens the column instead of leaving a gap the size of the widest word.
    /// </remarks>
    public static int TypeColumnWidth(IReadOnlyCollection<ConfigurableComponentKind> kinds) =>
        kinds.Max(k => TypeToken(k).Length);

    public static string Singular(ConfigurableComponentKind kind) => kind switch
    {
        ConfigurableComponentKind.CloudFlow => "flow",
        ConfigurableComponentKind.Workflow => "workflow",
        ConfigurableComponentKind.BusinessRule => "business rule",
        ConfigurableComponentKind.BusinessProcessFlow => "business process flow",
        ConfigurableComponentKind.Action => "action",
        ConfigurableComponentKind.PluginStep => "plugin step",
        ConfigurableComponentKind.EnvironmentVariable => "environment variable",
        ConfigurableComponentKind.ConnectionReference => "connection reference",
        _ => "component",
    };

    public static string Plural(ConfigurableComponentKind kind) => kind switch
    {
        ConfigurableComponentKind.CloudFlow => "cloud flows",
        ConfigurableComponentKind.Workflow => "classic workflows",
        ConfigurableComponentKind.BusinessRule => "business rules",
        ConfigurableComponentKind.BusinessProcessFlow => "business process flows",
        ConfigurableComponentKind.Action => "actions",
        ConfigurableComponentKind.PluginStep => "plugin steps",
        ConfigurableComponentKind.EnvironmentVariable => "environment variables",
        ConfigurableComponentKind.ConnectionReference => "connection references",
        _ => "components",
    };

    /// <summary>What to call the thing being picked, when several classes are in play.</summary>
    /// <remarks>
    /// Naming all six in a prompt title would be longer than the prompt. Narrowed by <c>--type</c> it
    /// says exactly what it is, and unnarrowed it says the only thing true of all of them.
    /// </remarks>
    public static string Singular(IReadOnlyCollection<ConfigurableComponentKind> kinds) =>
        kinds.Count == 1 ? Singular(kinds.First()) : "component";

    /// <inheritdoc cref="Singular(IReadOnlyCollection{ConfigurableComponentKind})"/>
    public static string Plural(IReadOnlyCollection<ConfigurableComponentKind> kinds)
    {
        if (kinds.Count == 1) return Plural(kinds.First());

        var names = kinds.Select(Plural).ToArray();

        return string.Join(", ", names[..^1]) + " or " + names[^1];
    }
}

/// <summary>Turns anything with an on and an off on or off, or reads its state.</summary>
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
    /// <summary>
    /// The classes <c>settings state</c> can narrow to (KTD25).
    /// </summary>
    /// <remarks>
    /// An enum rather than a string, so the parser rejects an unknown type and the help lists the valid
    /// ones with no hand-written check — the same thing that already makes <c>--value</c> on a flow a
    /// parse error. Spectre matches these case-insensitively, so the user types <c>bpf</c>.
    /// </remarks>
    public enum StateType { Flow, Workflow, Rule, Bpf, Action, Plugin }

    public sealed class Settings : SettingsComponentSettings
    {
        [CommandOption("--type <type>")]
        [Description("Narrow to one class: flow, workflow, rule, bpf, action, or plugin")]
        public StateType? Type { get; set; }

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

    protected override IReadOnlyCollection<ConfigurableComponentKind> KindsFor(Settings settings) =>
        settings.Type is { } type ? [KindFor(type)] : ConfigurableComponentKinds.WithState;

    internal static ConfigurableComponentKind KindFor(StateType type) => type switch
    {
        StateType.Flow => ConfigurableComponentKind.CloudFlow,
        StateType.Workflow => ConfigurableComponentKind.Workflow,
        StateType.Rule => ConfigurableComponentKind.BusinessRule,
        StateType.Bpf => ConfigurableComponentKind.BusinessProcessFlow,
        StateType.Action => ConfigurableComponentKind.Action,
        StateType.Plugin => ConfigurableComponentKind.PluginStep,
        // Unreachable: the parser only accepts the values above. An exception rather than a typed exit
        // code, because reaching it would mean the enum and this map disagree, not that input was bad.
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a state type."),
    };

    protected override bool IsWrite(Settings settings) => settings.On || settings.Off;

    const string LeaveIt = "leave it as it is";

    protected override async Task<bool> PromptForTargetAsync(
        SingleComponentOutcome current, Settings settings, EnvironmentInfo environment, CancellationToken ct)
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
        IOrganizationServiceAsync2 service, SolutionInventory inventory,
        IReadOnlyCollection<ConfigurableComponentKind> kinds,
        string name, Settings settings, RunMode mode, CancellationToken ct) =>
        SingleComponentService.ReadOrWriteStateAsync(
            service, inventory, kinds, name,
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
    /// <inheritdoc cref="SettingsStateCommand.StateType"/>
    public enum ValueType { EnvVar, ConnRef }

    public sealed class Settings : SettingsComponentSettings
    {
        [CommandOption("--type <type>")]
        [Description("Narrow to one class: envvar or connref")]
        public ValueType? Type { get; set; }

        [CommandOption("--value <value>")]
        [Description("The value to set, or the connection id to bind. Omit to read the current one")]
        public string? Value { get; set; }
    }

    protected override IReadOnlyCollection<ConfigurableComponentKind> KindsFor(Settings settings) =>
        settings.Type is { } type ? [KindFor(type)] : ConfigurableComponentKinds.WithValue;

    internal static ConfigurableComponentKind KindFor(ValueType type) => type switch
    {
        ValueType.EnvVar => ConfigurableComponentKind.EnvironmentVariable,
        ValueType.ConnRef => ConfigurableComponentKind.ConnectionReference,
        // Unreachable, for the same reason as the state types above.
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a value type."),
    };

    protected override bool IsWrite(Settings settings) => settings.Value is not null;

    protected override Task<bool> PromptForTargetAsync(
        SingleComponentOutcome current, Settings settings, EnvironmentInfo environment,
        CancellationToken ct) =>
        current.Component.Kind == ConfigurableComponentKind.ConnectionReference
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
    /// <summary>
    /// Offers the environment's connections for this reference's connector, rather than a typed id.
    /// </summary>
    /// <remarks>
    /// The menu itself lives in <see cref="ConnectionPicker"/>, shared with the capture path: a settings
    /// file's unbound references ask the same question, and two menus for one question would drift.
    /// </remarks>
    async Task<bool> PromptForConnectionAsync(
        SingleComponentOutcome current, Settings settings, EnvironmentInfo environment, CancellationToken ct)
    {
        var chosen = await ConnectionPicker.PickAsync(
            Console, environment, current.Component.ConnectorId,
            $"Bind {Markup.Escape(current.Component.Name)} to:", ct);

        if (chosen is null) return false;

        settings.Value = chosen;
        return true;
    }

    protected override (bool? Enabled, string? Value) WrittenBy(Settings settings) => (null, settings.Value);

    protected override Task<SingleComponentOutcome> RunAsync(
        IOrganizationServiceAsync2 service, SolutionInventory inventory,
        IReadOnlyCollection<ConfigurableComponentKind> kinds,
        string name, Settings settings, RunMode mode, CancellationToken ct) =>
        SingleComponentService.ReadOrWriteValueAsync(service, inventory, kinds, name, settings.Value, mode, ct);
}
