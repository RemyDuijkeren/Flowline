using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Configure;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;
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

    sealed class NoRemainingArguments : IRemainingArguments
    {
        public ILookup<string, string?> Parsed { get; } = Array.Empty<string>().ToLookup(x => x, x => (string?)x);
        public IReadOnlyList<string> Raw { get; } = [];
    }

    static CommandContext Context(string name) => new([], new NoRemainingArguments(), name, null);

    static (TestConsole Console, DataverseConnector Connector, ProfileResolutionService Profiles) Deps(bool interactive)
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = interactive;
        if (interactive) console.Interactive();

        var connector = new DataverseConnector(console, new HttpClient());
        return (console, connector, new ProfileResolutionService(console, connector, new FlowlineRuntimeOptions()));
    }

    // ── R9: the non-interactive half of the picker ───────────────────────────

    sealed class StateProbe(
        IAnsiConsole console, DataverseConnector connector, FlowlineRuntimeOptions options,
        ProfileResolutionService profiles, SubprocessCapture capture, NuGetVersionClient nuget)
        : SettingsStateCommand(console, connector, options, profiles, NullLoggerFactory.Instance, capture, nuget)
    {
        public TestConsole Out => (TestConsole)Console;

        public Task<string> PickAsync(ConfigurableComponentKind kind, IReadOnlyList<InventoryComponent> candidates) =>
            ResolveMissingNameAsync(kind, candidates, CancellationToken.None);
    }

    static StateProbe MakeStateProbe(bool interactive)
    {
        var (console, connector, profiles) = Deps(interactive);
        return new StateProbe(console, connector, new FlowlineRuntimeOptions(), profiles,
            new SubprocessCapture(console), new NuGetVersionClient(new HttpClient()));
    }

    static InventoryComponent Flow(string name, bool enabled = true) =>
        new(ConfigurableComponentKind.CloudFlow, name, Guid.NewGuid(), enabled);

    // An unattended caller must fail with the list, not hang on a prompt it cannot answer.
    [Fact]
    public async Task NoNameInANonInteractiveRun_ListsTheCandidatesAndFailsNamingTheArgument()
    {
        var probe = MakeStateProbe(interactive: false);

        var act = () => probe.PickAsync(ConfigurableComponentKind.CloudFlow,
            [Flow("Nightly reconciliation"), Flow("ApprovalFlow")]);

        var thrown = (await act.Should().ThrowAsync<FlowlineException>()).Which;
        thrown.ExitCode.Should().Be(ExitCode.ValidationFailed);
        thrown.Message.Should().Contain("flow");
    }

    [Fact]
    public async Task NoNameInANonInteractiveRun_PrintsEveryCandidateName()
    {
        var probe = MakeStateProbe(interactive: false);
        var console = probe.Out;

        try
        {
            await probe.PickAsync(ConfigurableComponentKind.CloudFlow,
                [Flow("Nightly reconciliation"), Flow("ApprovalFlow")]);
        }
        catch (FlowlineException)
        {
            // The throw is the subject of the test above; here the output is.
        }

        console.Output.Should().Contain("ApprovalFlow").And.Contain("Nightly reconciliation");
    }

    // Sorted, so a caller copying a name out of a long list can find it.
    [Fact]
    public async Task TheCandidateList_IsSortedCaseInsensitively()
    {
        var probe = MakeStateProbe(interactive: false);
        var console = probe.Out;

        try
        {
            await probe.PickAsync(ConfigurableComponentKind.CloudFlow,
                [Flow("zeta"), Flow("Alpha"), Flow("middle")]);
        }
        catch (FlowlineException) { }

        var output = console.Output;
        output.IndexOf("Alpha", StringComparison.Ordinal)
            .Should().BeLessThan(output.IndexOf("middle", StringComparison.Ordinal));
        output.IndexOf("middle", StringComparison.Ordinal)
            .Should().BeLessThan(output.IndexOf("zeta", StringComparison.Ordinal));
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

    // ── AE9: the bare branch form ────────────────────────────────────────────

    sealed class BranchProbe(
        IAnsiConsole console, FlowlineRuntimeOptions options, ProfileResolutionService profiles,
        SubprocessCapture capture, NuGetVersionClient nuget)
        : SettingsCommand(console, options, profiles, NullLoggerFactory.Instance, capture, nuget)
    {
        public TestConsole Out => (TestConsole)Console;

        public Task<int> RunAsync() =>
            ExecuteFlowlineAsync(Context("settings"), new Settings(), CancellationToken.None);
    }

    static BranchProbe MakeBranchProbe()
    {
        var (console, _, profiles) = Deps(interactive: false);
        return new BranchProbe(console, new FlowlineRuntimeOptions(), profiles,
            new SubprocessCapture(console), new NuGetVersionClient(new HttpClient()));
    }

    [Fact]
    public async Task TheBareBranchForm_FailsWithTheTypedCodeRatherThanSpectresGeneralError()
    {
        var probe = MakeBranchProbe();

        var act = () => probe.RunAsync();

        var thrown = (await act.Should().ThrowAsync<FlowlineException>()).Which;
        thrown.ExitCode.Should().Be(ExitCode.ValidationFailed);
        thrown.ExitCode.Should().NotBe(ExitCode.GeneralError);
    }

    [Fact]
    public async Task TheBareBranchForm_PrintsEveryOperationUnderItsGroup()
    {
        var probe = MakeBranchProbe();
        var console = probe.Out;

        try { await probe.RunAsync(); } catch (FlowlineException) { }

        var output = console.Output;
        output.Should().Contain("Whole file").And.Contain("One component");

        foreach (var operation in new[] { "push", "pull", "flow", "workflow", "plugin", "envvar", "connref" })
            output.Should().Contain(operation);

        // The grouping is the point: the two whole-file operations come before the five component kinds.
        output.IndexOf("Whole file", StringComparison.Ordinal)
            .Should().BeLessThan(output.IndexOf("One component", StringComparison.Ordinal));
    }
}
