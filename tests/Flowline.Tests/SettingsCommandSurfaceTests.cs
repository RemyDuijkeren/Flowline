using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Configure;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Flowline.Commands;

/// <summary>
/// The paths a pure helper cannot reach: what a command actually prints and throws.
/// </summary>
/// <remarks>
/// The 2026-09-10 review found these untested, and two real bugs had shipped behind them. Uses the same
/// probe-subclass pattern as <c>FlowlineCommandStandaloneTests</c>, which exposes a command's protected
/// members rather than driving the whole Spectre pipeline.
/// </remarks>
public class SettingsCommandSurfaceTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public SettingsCommandSurfaceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    static (TestConsole Console, DataverseConnector Connector, ProfileResolutionService Profiles) Deps(bool interactive)
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = interactive;
        if (interactive) console.Interactive();

        var connector = new DataverseConnector(console, new HttpClient());
        return (console, connector, new ProfileResolutionService(console, connector, new FlowlineRuntimeOptions()));
    }

    // ── R9: what a missing name does ────────────────────────────────────────

    sealed class StateProbe(
        IAnsiConsole console, DataverseConnector connector, FlowlineRuntimeOptions options,
        ProfileResolutionService profiles, SubprocessCapture capture, NuGetVersionClient nuget)
        : SettingsStateCommand(console, connector, options, profiles, NullLoggerFactory.Instance, capture, nuget)
    {
        public TestConsole Out => (TestConsole)Console;

        public void List(ConfigurableComponentKind kind, IReadOnlyList<InventoryComponent> candidates) =>
            ListCandidates([kind], candidates);

        public static string HeaderLabel(string label) => PickRow.Header(label).Label;

        public Task<InventoryComponent> PickAny(IReadOnlyList<InventoryComponent> candidates) =>
            PickAsync(ConfigurableComponentKinds.WithState, candidates, CancellationToken.None);

        public void ListAll(IReadOnlyList<InventoryComponent> candidates) =>
            ListCandidates(ConfigurableComponentKinds.WithState, candidates);

        public Task<bool> AskAsync(SingleComponentOutcome current, Settings settings) =>
            PromptForTargetAsync(current, settings, new EnvironmentInfo(), CancellationToken.None);

        public bool Writes(Settings settings) => IsWrite(settings);
    }

    static StateProbe MakeStateProbe(bool interactive)
    {
        var (console, connector, profiles) = Deps(interactive);
        return new StateProbe(console, connector, new FlowlineRuntimeOptions(), profiles,
            new SubprocessCapture(console), new NuGetVersionClient(new HttpClient()));
    }

    static InventoryComponent Flow(string name, bool enabled = true) =>
        new(ConfigurableComponentKind.CloudFlow, name, Guid.NewGuid(), enabled);

    // Suspended reads as not-enabled, which is exactly why it needs its own flag to stay visible.
    static InventoryComponent Suspended(string name) =>
        new(ConfigurableComponentKind.CloudFlow, name, Guid.NewGuid(), Enabled: false, Suspended: true);

    // An unattended caller asking "what is in here" gets the answer, not a failure. This used to print
    // the names and then throw ValidationFailed, which made a legitimate inventory question look like a
    // malformed invocation.
    [Fact]
    public void NoNameInANonInteractiveRun_PrintsEveryCandidateName()
    {
        var probe = MakeStateProbe(interactive: false);

        probe.List(ConfigurableComponentKind.CloudFlow, [Flow("Nightly reconciliation"), Flow("ApprovalFlow")]);

        probe.Out.Output.Should().Contain("ApprovalFlow").And.Contain("Nightly reconciliation");
    }

    // Sorted, so a caller copying a name out of a long list can find it.
    [Fact]
    public void TheCandidateList_IsSortedCaseInsensitively()
    {
        var probe = MakeStateProbe(interactive: false);

        probe.List(ConfigurableComponentKind.CloudFlow, [Flow("zeta"), Flow("Alpha"), Flow("middle")]);

        var output = probe.Out.Output;
        output.IndexOf("Alpha", StringComparison.Ordinal)
            .Should().BeLessThan(output.IndexOf("middle", StringComparison.Ordinal));
        output.IndexOf("middle", StringComparison.Ordinal)
            .Should().BeLessThan(output.IndexOf("zeta", StringComparison.Ordinal));
    }

    [Fact]
    public void AStateKindList_ShowsWhatEachOneCurrentlyIs()
    {
        var probe = MakeStateProbe(interactive: false);

        probe.List(ConfigurableComponentKind.CloudFlow, [Flow("Runs", enabled: true), Flow("Stopped", enabled: false)]);

        probe.Out.Output.Should().Contain("\u25cf on   Runs").And.Contain("\u25cb off  Stopped");
    }

    // State first because it is the only column a terminal cannot push off the line. A plugin step is
    // named for its class and message, runs past a hundred characters, and used to wrap its trailing
    // state onto a line of its own.
    [Fact]
    public void AStateKindList_PutsTheStateBeforeTheName()
    {
        var probe = MakeStateProbe(interactive: false);

        probe.List(ConfigurableComponentKind.CloudFlow, [Flow("Nightly reconciliation")]);

        probe.Out.Lines.Should().Contain(l => l.StartsWith("\u25cf on   Nightly", StringComparison.Ordinal));
    }

    // On and off pad to each other so their names line up. Suspended is left long on purpose: padding
    // every row to its width costs twelve columns on rows that already overflow.
    [Fact]
    public void OnAndOff_AlignTheirNames_AndSuspendedDoesNot()
    {
        var probe = MakeStateProbe(interactive: false);

        probe.List(ConfigurableComponentKind.CloudFlow,
            [Flow("Aaa", enabled: true), Flow("Bbb", enabled: false), Suspended("Ccc")]);

        var output = probe.Out.Output;
        output.Should().Contain("\u25cf on   Aaa").And.Contain("\u25cb off  Bbb");
        output.Should().Contain("\u25d0 suspended  Ccc");
    }

    // Suspended is not a second kind of off. Dataverse flattens it to not-enabled, and someone who reads
    // a stopped-itself flow as off turns it on and is surprised when it stops again.
    [Fact]
    public void ASuspendedComponent_ReadsAsSuspendedNotOff()
    {
        var probe = MakeStateProbe(interactive: false);

        probe.List(ConfigurableComponentKind.CloudFlow, [Suspended("Stopped itself")]);

        probe.Out.Output.Should().Contain("suspended").And.NotContain("off");
    }

    // The interesting ones first: a component that stopped itself is the one nobody meant, an off one is
    // usually why the list was opened, and the rest are scrolled past.
    [Fact]
    public void TheCandidateList_PutsSuspendedThenOffThenOn()
    {
        var probe = MakeStateProbe(interactive: false);

        var ordered = SettingsComponentOutcomes.Ordered(
            [Flow("on one", enabled: true), Flow("off one", enabled: false), Suspended("suspended one")]);

        ordered.Select(c => c.Name).Should().Equal("suspended one", "off one", "on one");
    }

    [Fact]
    public void WithinAState_TheListStaysInNameOrder()
    {
        var ordered = SettingsComponentOutcomes.Ordered(
            [Flow("zeta", enabled: false), Flow("Alpha", enabled: false)]);

        ordered.Select(c => c.Name).Should().Equal("Alpha", "zeta");
    }

    // Colour reinforces the shape rather than carrying it, so the state still reads when the selection
    // highlight repaints the row.
    [Fact]
    public void ThePickerLabel_ColoursTheStateAndEscapesTheName()
    {
        var label = SettingsComponentOutcomes.DescribeCandidate(
            Flow("[Account] OnCreate", enabled: false), typeWidth: 4);

        label.Should().StartWith("[red]\u25cb off[/]  flow");
        label.Should().Contain("[[Account]] OnCreate");
    }

    // The exposure line: one named read may print a secret, a whole-solution listing may not.
    [Fact]
    public void AnEnvironmentVariableList_ShowsNamesOnly()
    {
        var probe = MakeStateProbe(interactive: false);
        var variable = new InventoryComponent(
            ConfigurableComponentKind.EnvironmentVariable, "av_ApiKey", Guid.NewGuid(), null, "s3cret");

        probe.List(ConfigurableComponentKind.EnvironmentVariable, [variable]);

        probe.Out.Output.Should().Contain("av_ApiKey").And.NotContain("s3cret");
    }

    // A bracketed name is ordinary in the maker portal. The listing writes plain text, so it must reach
    // the output verbatim rather than being read as a style tag or escaped into doubled brackets.
    [Fact]
    public void ABracketedName_SurvivesTheListingVerbatim()
    {
        var probe = MakeStateProbe(interactive: false);

        probe.List(ConfigurableComponentKind.CloudFlow, [Flow("[Account] OnCreate")]);

        probe.Out.Output.Should().Contain("[Account] OnCreate");
    }

    // Picking a row has to carry which row it was, not just its name. A business rule and a classic
    // workflow can share a name — "Account - set name" does in a real solution — and forwarding only the
    // name re-resolved it across every class and failed as ambiguous, telling someone who had just
    // pointed at one row to go and rename something in Dataverse.
    [Fact]
    public async Task PickingAComponent_CarriesWhichOneItWasNotJustItsName()
    {
        var probe = MakeStateProbe(interactive: true);
        probe.Out.Input.PushKey(ConsoleKey.Enter);

        var rule = new InventoryComponent(
            ConfigurableComponentKind.BusinessRule, "Account - set name", Guid.NewGuid(), false);
        var workflow = new InventoryComponent(
            ConfigurableComponentKind.Workflow, "Account - set name", Guid.NewGuid(), true);

        // Off sorts first, so Enter takes the business rule.
        var picked = await probe.PickAny([workflow, rule]);

        picked.Kind.Should().Be(ConfigurableComponentKind.BusinessRule);
        picked.Id.Should().Be(rule.Id);
    }

    // ── R9d: grouping by shared leading text ─────────────────────────────────

    static InventoryComponent Step(string name, bool on = true) =>
        new(ConfigurableComponentKind.PluginStep, name, Guid.NewGuid(), on);

    const string Sync = "DWE_Base.Plugins.SyncOrderLines.";
    const string Calc = "DWE_Base.Plugins.CalculateTax.";

    // The point of grouping: the text every sibling repeats moves onto a header, so the rows underneath
    // carry only what differs and the line breaks where it means something.
    [Fact]
    public void ComponentsSharingALeadingPart_AreGroupedUnderIt()
    {
        var blocks = SettingsComponentOutcomes.Grouped([
            Step(Sync + "OrderLineDeleteGuard: Delete of salesorderdetail"),
            Step(Sync + "OrderLineFromWorkOrderProduct: Create of msdyn_workorderproduct"),
            Step(Sync + "OrderLinesFromWorkOrder: Update of msdyn_workorder"),
            Step(Calc + "CalculateSalesTax: Create of salesorderdetail"),
            Step(Calc + "CalculateInvoiceTax: Create of invoicedetail"),
            Step(Calc + "StampSalesTaxCode: Update of salesorderdetail"),
        ]);

        blocks.Select(b => b.Label).Should().BeEquivalentTo([Calc, Sync]);
        blocks.Should().OnlyContain(b => b.Members.Count == 3);
    }

    // A heading costs a line and saves its own length per member, so a group has to pay for the row it
    // spends. Two flows sharing "[SalesOrderDetail] " save nineteen characters twice on rows that were
    // never going to wrap; that is not worth a heading.
    [Fact]
    public void AGroupThatWouldSaveLessThanItCosts_IsNotAGroup()
    {
        var blocks = SettingsComponentOutcomes.Grouped([
            Flow("[SalesOrderDetail] OnCreate | Create WorkOrder"),
            Flow("[SalesOrderDetail] OnDelete | Remove WorkOrder"),
        ]);

        blocks.Should().HaveCount(2).And.OnlyContain(b => b.Label == null);
    }

    // The same two components pay once the shared text is long enough to be worth removing twice.
    [Fact]
    public void TwoComponents_DoGroup_WhenTheSharedTextIsLongEnough()
    {
        const string longPrefix = "Contoso.Integration.Plugins.SalesOrderProcessing.Handlers.";

        var blocks = SettingsComponentOutcomes.Grouped([
            Step(longPrefix + "AlphaPlugin: Create of a"),
            Step(longPrefix + "BetaPlugin: Create of b"),
        ]);

        blocks.Should().ContainSingle().Which.Label.Should().Be(longPrefix);
    }

    // Cut only at a delimiter, so a heading is never half a word.
    [Fact]
    public void AGroupLabel_EndsAtADelimiter()
    {
        var blocks = SettingsComponentOutcomes.Grouped([
            Step("Contoso.Integration.Plugins.OrderLineAlpha: Create of a"),
            Step("Contoso.Integration.Plugins.OrderLineBeta: Create of b"),
            Step("Contoso.Integration.Plugins.OrderLineGamma: Create of c"),
        ]);

        blocks.Should().ContainSingle().Which.Label.Should().Be("Contoso.Integration.Plugins.");
    }

    // A solution whose names share nothing gets no headers, and the list is exactly as it was.
    [Fact]
    public void ComponentsSharingNothing_AreNotGrouped()
    {
        var blocks = SettingsComponentOutcomes.Grouped([Flow("Nightly"), Step("Contoso: Create of a")]);

        blocks.Should().HaveCount(2).And.OnlyContain(b => b.Label == null && b.Members.Count == 1);
    }

    // A group is one contiguous block, so its rows cannot be split across the state tiers. Ranking whole
    // blocks by their best member keeps the promise the ordering was for, and an ungrouped component wins
    // a tie: suspended, then a group holding a suspended one, then off, then a group holding an off one.
    [Fact]
    public void Blocks_AreOrderedByTheirMostInterestingMember_UngroupedFirstOnATie()
    {
        var blocks = SettingsComponentOutcomes.Grouped([
            Step(Calc + "AllOnAlpha: Create of a"),
            Step(Calc + "AllOnBeta: Create of b"),
            Step(Calc + "AllOnGamma: Create of c"),
            Step(Sync + "HasAnOff: Delete of d", on: false),
            Step(Sync + "AlsoOn: Create of e"),
            Step(Sync + "AlsoOnToo: Create of f"),
            // Shares its leading text with nothing, so it stays a block of one.
            Step("Zebra.OffOne: Update of g", on: false),
        ]);

        // Both the lone component and the SyncOrderLines group are off, and the lone one wins the tie.
        // CalculateTax is entirely on, so it sinks below both.
        blocks.Select(b => b.Label ?? b.Members[0].Name)
            .Should().Equal("Zebra.OffOne: Update of g", Sync, Calc);
    }

    [Fact]
    public void WithinAGroup_MembersKeepTheStateOrdering()
    {
        var blocks = SettingsComponentOutcomes.Grouped([
            Step(Calc + "OnOne: Create of a"),
            Step(Calc + "OffOne: Create of b", on: false),
            Step(Calc + "OnTwo: Create of c"),
        ]);

        blocks.Should().ContainSingle().Which.Members
            .Select(m => m.Name[Calc.Length..])
            .Should().Equal("OffOne: Create of b", "OnOne: Create of a", "OnTwo: Create of c");
    }

    // A heading nobody can read is a gap, not a label, and dim renders near-invisible on a dark terminal.
    // It spends no colour either: green and red already mean state and the highlight means the cursor, so
    // a heading in a third colour competes with the row you are pointing at and reads as selectable.
    [Fact]
    public void AGroupHeading_IsBoldAndSpendsNoColour()
    {
        var header = StateProbe.HeaderLabel("DWE_Base.Plugins.CalculateTax.");

        header.Should().Be("[bold]DWE_Base.Plugins.CalculateTax.[/]");
    }

    // The header carries the shared text, so the row under it must not repeat it.
    [Fact]
    public void AGroupedRow_DropsTheTextTheHeaderAlreadyShows()
    {
        SettingsComponentOutcomes
            .DescribeCandidate(Step(Sync + "OrderLineDeleteGuard: Delete of salesorderdetail"), null, Sync)
            .Should().Be("[green]● on [/]  OrderLineDeleteGuard: Delete of salesorderdetail");
    }

    // ── Esc backs out of the run ─────────────────────────────────────────────

    // Ctrl+C was the only way out of a picker. Esc is what people reach for, and it has to stop the run
    // rather than resolve to a choice.
    [Fact]
    public async Task EscapeAtThePicker_CancelsTheRun()
    {
        var probe = MakeStateProbe(interactive: true);
        probe.Out.Input.PushKey(ConsoleKey.Escape);

        var act = () => probe.PickAny([Flow("Nightly reconciliation")]);

        await act.Should().ThrowAsync<PromptCancelledException>();
    }

    [Fact]
    public async Task EscapeAtTheStatePrompt_CancelsTheRun()
    {
        var probe = MakeStateProbe(interactive: true);
        probe.Out.Input.PushKey(ConsoleKey.Escape);

        var act = () => probe.AskAsync(Reading(enabled: true), new SettingsStateCommand.Settings());

        await act.Should().ThrowAsync<PromptCancelledException>();
    }

    [Fact]
    public async Task EscapeAtTheEnvironmentPicker_CancelsTheRun()
    {
        var probe = MakePullProbe(interactive: true);
        probe.Out.Input.PushKey(ConsoleKey.Escape);

        var act = () => probe.ChooseAsync(EnvironmentRole.Dev, EnvironmentRole.Prod);

        await act.Should().ThrowAsync<PromptCancelledException>();
    }

    // It derives from OperationCanceledException so the capture sweep stops rather than treating Esc as
    // one environment failing and moving on to the next. It also has to keep its own identity, because
    // the timeout matcher reads a plain OperationCanceledException raised without the Ctrl+C token as a
    // timed-out Dataverse request.
    [Fact]
    public void APromptCancellation_IsACancellationAndKeepsItsOwnType()
    {
        var cancelled = new PromptCancelledException();

        cancelled.Should().BeAssignableTo<OperationCanceledException>();
        DataverseTimeout.Matches(cancelled, userCancelled: false).Should().BeTrue(
            "the matcher cannot tell them apart, which is why the handler arm sits above it");
    }

    // The answer to "set it to" is the same fact the row above just showed, so it looks the same.
    [Fact]
    public void TheStatePromptChoices_CarryTheSameShapeAndColourAsTheList()
    {
        SettingsComponentOutcomes.DescribeState(on: true).Should().Be("[green]● on[/]");
        SettingsComponentOutcomes.DescribeState(on: false).Should().Be("[red]○ off[/]");
    }

    // ── KTD25: the type column ───────────────────────────────────────────────

    static InventoryComponent Rule(string name, bool enabled = true) =>
        new(ConfigurableComponentKind.BusinessRule, name, Guid.NewGuid(), enabled);

    // The column is what makes one command over six classes readable, and because the picker's search
    // matches the rendered label, it is also the filter: typing "rule" narrows the list with no flag.
    [Fact]
    public void AMixedList_CarriesEachComponentsType()
    {
        var probe = MakeStateProbe(interactive: false);

        probe.ListAll([Flow("Nightly", enabled: false), Rule("contoso_ShowHide", enabled: false)]);

        probe.Out.Output.Should().Contain("flow").And.Contain("rule");
    }

    // Narrowing tightens the column rather than leaving a gap the width of the longest word there is.
    [Fact]
    public void TheTypeColumn_IsMeasuredFromTheClassesInPlay()
    {
        SettingsComponentNames.TypeColumnWidth(ConfigurableComponentKinds.WithState)
            .Should().Be("workflow".Length);

        SettingsComponentNames.TypeColumnWidth(ConfigurableComponentKinds.WithValue)
            .Should().Be("connref".Length);
    }

    // Narrowed all the way to one class the column says the same word on every row, so it goes. That is
    // ten characters back on --type plugin, which is the run whose names are longest.
    [Fact]
    public void OneClassInPlay_HasNoTypeColumnAtAll()
    {
        SettingsComponentNames.TypeColumnWidth([ConfigurableComponentKind.PluginStep]).Should().BeNull();

        SettingsComponentOutcomes.Describe(Flow("Nightly", enabled: false), typeWidth: null)
            .Should().Be("○ off  Nightly");

        SettingsComponentOutcomes.DescribeCandidate(Flow("Nightly", enabled: false), typeWidth: null)
            .Should().Be("[red]○ off[/]  Nightly");
    }

    // State first, then type, then the name. The order matters: the name is the part that wraps.
    [Fact]
    public void AStateLine_ReadsStateThenTypeThenName()
    {
        SettingsComponentOutcomes.Describe(Rule("contoso_ShowHide", enabled: false), typeWidth: 8)
            .Should().Be("○ off  rule      contoso_ShowHide");
    }

    // Ordering is by state and then name, deliberately not grouped by type: what you came to fix
    // belongs at the top, whatever kind of thing it is.
    [Fact]
    public void AMixedList_StaysOrderedByStateNotByType()
    {
        var ordered = SettingsComponentOutcomes.Ordered(
            [Flow("zeta flow", enabled: true), Rule("alpha rule", enabled: false)]);

        ordered.Select(c => c.Name).Should().Equal("alpha rule", "zeta flow");
    }

    // ── R9: the prompt that follows an interactive read ──────────────────────

    static SingleComponentOutcome Reading(bool enabled) =>
        new(Flow("Nightly reconciliation", enabled), SingleComponentActionKind.Read, PriorEnabled: enabled);

    // The whole point of the change: the first choice is the flip, so one Enter turns it around.
    [Fact]
    public async Task TheStatePrompt_DefaultsToTheOppositeOfWhatItIs()
    {
        var probe = MakeStateProbe(interactive: true);
        probe.Out.Input.PushKey(ConsoleKey.Enter);
        var settings = new SettingsStateCommand.Settings();

        var writes = await probe.AskAsync(Reading(enabled: true), settings);

        writes.Should().BeTrue();
        settings.Off.Should().BeTrue();
        settings.On.Should().BeFalse();
        probe.Writes(settings).Should().BeTrue();
    }

    [Fact]
    public async Task TheStatePrompt_OffersToTurnOnWhatIsOff()
    {
        var probe = MakeStateProbe(interactive: true);
        probe.Out.Input.PushKey(ConsoleKey.Enter);
        var settings = new SettingsStateCommand.Settings();

        await probe.AskAsync(Reading(enabled: false), settings);

        settings.On.Should().BeTrue();
    }

    // Backing out leaves the flags alone, so the caller reports a read and writes nothing.
    [Fact]
    public async Task DecliningTheStatePrompt_AsksForNoWrite()
    {
        var probe = MakeStateProbe(interactive: true);
        probe.Out.Input.PushKey(ConsoleKey.DownArrow);
        probe.Out.Input.PushKey(ConsoleKey.DownArrow);
        probe.Out.Input.PushKey(ConsoleKey.Enter);
        var settings = new SettingsStateCommand.Settings();

        var writes = await probe.AskAsync(Reading(enabled: true), settings);

        writes.Should().BeFalse();
        probe.Writes(settings).Should().BeFalse();
    }

    // ── R16: what a pull with no environment does ────────────────────────────

    sealed class PullProbe(
        IAnsiConsole console, DataverseConnector connector, FlowlineRuntimeOptions options,
        ProfileResolutionService profiles, SubprocessCapture capture, NuGetVersionClient nuget)
        : SettingsPullCommand(console, connector, options, profiles, NullLoggerFactory.Instance, capture, nuget)
    {
        public TestConsole Out => (TestConsole)Console;

        public Task<IReadOnlyList<EnvironmentRole>> ChooseAsync(params EnvironmentRole[] configured) =>
            ChooseRolesAsync(configured, CancellationToken.None);
    }

    static PullProbe MakePullProbe(bool interactive)
    {
        var (console, connector, profiles) = Deps(interactive);
        return new PullProbe(console, connector, new FlowlineRuntimeOptions(), profiles,
            new SubprocessCapture(console), new NuGetVersionClient(new HttpClient()));
    }

    // The bare word used to sweep every configured environment unattended. That is the widest and slowest
    // thing this command does, so a script that meant one environment should hear about it rather than
    // wait through four auth flows.
    [Fact]
    public async Task NoEnvironmentInANonInteractiveRun_FailsNamingTheArgument()
    {
        var probe = MakePullProbe(interactive: false);

        var act = () => probe.ChooseAsync(EnvironmentRole.Dev, EnvironmentRole.Prod);

        var thrown = (await act.Should().ThrowAsync<FlowlineException>()).Which;
        thrown.ExitCode.Should().Be(ExitCode.ValidationFailed);
        thrown.Message.Should().Contain("settings pull <target>").And.Contain("dev").And.Contain("prod");
    }

    // Everything starts selected, so one Enter still captures the lot. The change makes the sweep
    // visible, not harder.
    [Fact]
    public async Task TheEnvironmentPicker_StartsWithEveryConfiguredRoleSelected()
    {
        var probe = MakePullProbe(interactive: true);
        probe.Out.Input.PushKey(ConsoleKey.Enter);

        var chosen = await probe.ChooseAsync(EnvironmentRole.Dev, EnvironmentRole.Uat, EnvironmentRole.Prod);

        chosen.Should().BeEquivalentTo([EnvironmentRole.Dev, EnvironmentRole.Uat, EnvironmentRole.Prod]);
    }

    // Deselecting has to actually narrow the run, or the picker is decoration. Which role the toggle
    // lands on is Spectre's business — the cursor starts on the last pre-selected item — so what is
    // pinned here is that one keystroke drops one environment from the capture.
    [Fact]
    public async Task DeselectingARole_LeavesItOutOfTheCapture()
    {
        var probe = MakePullProbe(interactive: true);
        var input = probe.Out.Input;
        input.PushKey(ConsoleKey.Spacebar);
        input.PushKey(ConsoleKey.Enter);

        var chosen = await probe.ChooseAsync(EnvironmentRole.Dev, EnvironmentRole.Prod);

        chosen.Should().ContainSingle().Which.Should().BeOneOf(EnvironmentRole.Dev, EnvironmentRole.Prod);
    }

    // Picking nothing is an answer. Without it the only way out of the list is Ctrl+C.
    [Fact]
    public async Task DeselectingEverything_CapturesNothing()
    {
        var probe = MakePullProbe(interactive: true);
        var input = probe.Out.Input;
        input.PushKey(ConsoleKey.Spacebar);
        input.PushKey(ConsoleKey.Enter);

        (await probe.ChooseAsync(EnvironmentRole.Dev)).Should().BeEmpty();
    }

    // ── The standalone predicate the component operations use ────────────────

    // The bug this pins: keying stand-alone on --solution-name made the error naming that flag
    // unreachable. Without the flag the run was judged project mode, and the base class's project gate
    // said "No Flowline project found — run flowline clone" — telling someone outside a project to create
    // one rather than to pass the one flag that would have worked.
    [Fact]
    public void AComponentOperationOutsideAProject_IsStandaloneEvenWithNoSolutionName() =>
        SettingsSupport.ResolveComponentStandalone(_dir).Should().BeTrue();

    [Fact]
    public void AComponentOperationInsideAProject_IsNotStandalone()
    {
        File.WriteAllText(Path.Combine(_dir, ProjectConfig.s_configFileName), "{}");

        SettingsSupport.ResolveComponentStandalone(_dir).Should().BeFalse();
    }

    // Push keeps the flag-keyed rule, because there the flag is what distinguishes the two modes.
    [Fact]
    public void TheComponentRuleAndThePushRule_DifferOutsideAProjectWithNoFlag()
    {
        SettingsSupport.ResolveComponentStandalone(_dir).Should().BeTrue();
        SettingsSupport.ResolveStandalone(null, null, _dir).Should().BeFalse();
    }
}
