using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Configure;
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
            ListCandidates(kind, candidates);

        public Task<bool> AskAsync(SingleComponentOutcome current, Settings settings) =>
            PromptForTargetAsync(ConfigurableComponentKind.CloudFlow, current, settings,
                new EnvironmentInfo(), CancellationToken.None);

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

        probe.Out.Lines.Should().Contain(l => l.StartsWith("\u25cf on", StringComparison.Ordinal));
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
            [Flow("on one", enabled: true), Flow("off one", enabled: false), Suspended("suspended one")],
            ConfigurableComponentKind.CloudFlow);

        ordered.Select(c => c.Name).Should().Equal("suspended one", "off one", "on one");
    }

    [Fact]
    public void WithinAState_TheListStaysInNameOrder()
    {
        var ordered = SettingsComponentOutcomes.Ordered(
            [Flow("zeta", enabled: false), Flow("Alpha", enabled: false)],
            ConfigurableComponentKind.CloudFlow);

        ordered.Select(c => c.Name).Should().Equal("Alpha", "zeta");
    }

    // Colour reinforces the shape rather than carrying it, so the state still reads when the selection
    // highlight repaints the row.
    [Fact]
    public void ThePickerLabel_ColoursTheStateAndEscapesTheName()
    {
        var label = SettingsComponentOutcomes.DescribeCandidate(
            Flow("[Account] OnCreate", enabled: false), ConfigurableComponentKind.CloudFlow);

        label.Should().StartWith("[red]\u25cb off[/]");
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
