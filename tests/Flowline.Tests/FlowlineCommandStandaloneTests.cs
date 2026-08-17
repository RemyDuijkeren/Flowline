using Flowline;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using Flowline.Validation;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace Flowline.Commands;

// Covers U2: the standalone predicate FlowlineCommand<TSettings> uses to branch project-root
// resolution (R3) and CheckSetupAsync (R3, R11) without disturbing the shared pipeline — ValidateForce,
// InvocationLogger, the activity span, and the welcome-screen decision all stay on the path both modes
// share (KTD1). Uses the same test-double pattern as FlowlineCommandTests.TestCommand.
public class FlowlineCommandStandaloneTests
{
    // Spectre.Console.Cli exposes no public IRemainingArguments implementation, and CommandContext's
    // constructor requires one even though none of these tests pass "--" arguments.
    sealed class NoRemainingArguments : IRemainingArguments
    {
        public ILookup<string, string?> Parsed { get; } = Array.Empty<string>().ToLookup(x => x, x => (string?)x);
        public IReadOnlyList<string> Raw { get; } = [];
    }

    sealed class TestCommand(
        IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService,
        ILoggerFactory loggerFactory, SubprocessCapture capture, NuGetVersionClient nuGetVersionClient)
        : FlowlineCommand<FlowlineSettings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture, nuGetVersionClient)
    {
        public bool Standalone { get; set; }
        public bool RequiresProjectValue { get; set; } = true;
        public string[] ForceSpecifiers { get; set; } = [];

        // Isolates RootFolder resolution (and force validation) from the real git/dotnet/pac probes —
        // scenarios that only care about which branch RootFolder takes must not also depend on the
        // ambient test-run working directory happening to satisfy those probes.
        public bool SkipSetup { get; set; }

        protected override bool IsStandalone(FlowlineSettings settings) => Standalone;
        protected override bool RequiresProject => RequiresProjectValue;
        protected override string[] ValidForceSpecifiers => ForceSpecifiers;

        protected override Task CheckSetupAsync(FlowlineSettings settings, CancellationToken cancellationToken) =>
            SkipSetup ? Task.CompletedTask : base.CheckSetupAsync(settings, cancellationToken);

        protected override Task<int> ExecuteFlowlineAsync(CommandContext context, FlowlineSettings settings, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        // Exposes the protected pipeline entry points for tests.
        public Task<int> RunAsync(CommandContext context, FlowlineSettings settings, CancellationToken cancellationToken) =>
            ExecuteAsync(context, settings, cancellationToken);

        public Task RunCheckSetupAsync(FlowlineSettings settings, CancellationToken cancellationToken) =>
            CheckSetupAsync(settings, cancellationToken);

        public string ResolvedRootFolder => RootFolder;

        // Standalone populates this from the one tool it probes (pac), so InvocationLogger clears its
        // null guard and a standalone run is still observable in telemetry.
        public FlowlineToolVersions? ToolVersionsValue => RuntimeOptions.ToolVersions;

        // Exposes the shared helper push/generate also call, so its shape is asserted directly rather
        // than only through a CheckSetupAsync run that needs pac on the box.
        public void RunApplyStandaloneToolVersions(ToolCheckResult pac) => ApplyStandaloneToolVersions(pac);
    }

    static TestCommand MakeCommand()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = false;
        var connector = new DataverseConnector(console, new HttpClient());
        var profileResolutionService = new ProfileResolutionService(console, connector, new FlowlineRuntimeOptions());
        return new TestCommand(console, new FlowlineRuntimeOptions(), profileResolutionService,
            NullLoggerFactory.Instance, new SubprocessCapture(console), new NuGetVersionClient(new HttpClient()));
    }

    static CommandContext MakeContext(string name = "test-command") =>
        new(Array.Empty<string>(), new NoRemainingArguments(), name, null);

    // ── R3 / KTD1: RootFolder resolution consults IsStandalone OR !RequiresProject ──────────────

    [Fact]
    public async Task ExecuteAsync_PredicateFalseAndNoProject_ThrowsConfigInvalidWithExistingMessage()
    {
        var command = MakeCommand();
        command.SkipSetup = true; // this scenario must throw before setup runs anyway.
        var settings = new FlowlineSettings();

        var act = () => command.RunAsync(MakeContext(), settings, CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.Should().Match<FlowlineException>(e =>
                e.ExitCode == ExitCode.ConfigInvalid &&
                e.Message == "No Flowline project found — run 'flowline clone' to set up a project.");
    }

    [Fact]
    public async Task ExecuteAsync_PredicateTrueAndNoProject_ResolvesRootFolderToWorkingDirectoryAndDoesNotThrow()
    {
        var command = MakeCommand();
        command.Standalone = true;
        command.SkipSetup = true;
        var settings = new FlowlineSettings();

        var act = () => command.RunAsync(MakeContext(), settings, CancellationToken.None);

        await act.Should().NotThrowAsync();
        command.ResolvedRootFolder.Should().Be(Directory.GetCurrentDirectory());
    }

    [Fact]
    public async Task ExecuteAsync_RequiresProjectFalseAndPredicateFalse_StillResolvesToWorkingDirectory()
    {
        // Regression guard for CloneCommand/InitCommand/SlnAddCommand/ScaffoldCommand: none of them set
        // the new standalone predicate — they rely solely on the pre-existing RequiresProject => false.
        var command = MakeCommand();
        command.RequiresProjectValue = false;
        command.SkipSetup = true;
        var settings = new FlowlineSettings();

        var act = () => command.RunAsync(MakeContext(), settings, CancellationToken.None);

        await act.Should().NotThrowAsync();
        command.ResolvedRootFolder.Should().Be(Directory.GetCurrentDirectory());
    }

    // ── R11: --force validation still runs on the shared path in standalone ─────────────────────

    [Fact]
    public async Task ExecuteAsync_StandaloneInvalidForceValue_RejectsWithTheCommandsOwnSpecifierList()
    {
        var command = MakeCommand();
        command.Standalone = true;
        command.SkipSetup = true;
        command.ForceSpecifiers = ["delete-orphans", "all"];
        var settings = new FlowlineSettings { Force = ["bogus"] };

        var act = () => command.RunAsync(MakeContext("deploy"), settings, CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.Should().Match<FlowlineException>(e =>
                e.ExitCode == ExitCode.ValidationFailed &&
                e.Message.Contains("delete-orphans") && e.Message.Contains("deploy"));
    }

    // ── R3: standalone setup skips the git / git-repo check ──────────────────────────────────────

    [Fact]
    public async Task CheckSetupAsync_Standalone_DoesNotRequireAGitRepository()
    {
        // The real ambient working directory (the test project's build output folder) is not itself a
        // git repo — confirms the assumption the assertion below relies on.
        Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), ".git")).Should().BeFalse();

        var command = MakeCommand();
        command.Standalone = true;
        var settings = new FlowlineSettings { NoCache = true };

        // The standalone branch still shells out to `pac` (EnsurePacCliAsync) — a runner with no PAC
        // CLI installed (CI's ubuntu-latest has none) throws for that unrelated reason. What this test
        // asserts is narrower: whichever way it fails, it must never be the git-repo message, because
        // that message can only come from EnsureGitRepoAsync, which standalone must never call.
        try
        {
            await command.RunCheckSetupAsync(settings, CancellationToken.None);
        }
        catch (FlowlineException ex)
        {
            ex.Message.Should().NotBe("No Git repo found. Run 'git init' or 'git clone' first.");
            return; // No pac on this runner — the git-repo assertion above is all this case can prove.
        }

        // Telemetry: standalone fills ToolVersions from the one tool it probes, so InvocationLogger
        // clears its null guard. Dotnet and git stay null because standalone never checks them —
        // "not checked", not a placeholder that would read as a real version downstream.
        command.ToolVersionsValue.Should().NotBeNull();
        command.ToolVersionsValue!.PacVersion.Should().NotBeNullOrWhiteSpace();
        command.ToolVersionsValue.FlowlineVersion.Should().NotBeNullOrWhiteSpace();
        command.ToolVersionsValue.DotNetVersion.Should().BeNull();
        command.ToolVersionsValue.GitVersion.Should().BeNull();
        command.ToolVersionsValue.GitBranch.Should().BeNull();
    }

    // ── Telemetry: standalone reports what it probed, and nothing it didn't ─────────────────────

    [Fact]
    public void ApplyStandaloneToolVersions_RecordsPacAndFlowline_AndLeavesUncheckedToolsNull()
    {
        var command = MakeCommand();

        command.RunApplyStandaloneToolVersions(new ToolCheckResult { Version = "1.2.3", InstallType = "Dotnet Tool (.NET)" });

        // Non-null is the whole point: InvocationLogger returns at its null guard otherwise, and a
        // standalone run would emit no invocation log or activity tags at all.
        command.ToolVersionsValue.Should().NotBeNull();
        command.ToolVersionsValue!.PacVersion.Should().Be("1.2.3");
        command.ToolVersionsValue.PacInstallType.Should().Be("Dotnet Tool (.NET)");
        command.ToolVersionsValue.FlowlineVersion.Should().NotBeNullOrWhiteSpace();

        // Standalone probes neither, so these must read "not checked" rather than carrying a
        // placeholder that downstream consumers would mistake for a real version.
        command.ToolVersionsValue.DotNetVersion.Should().BeNull();
        command.ToolVersionsValue.GitVersion.Should().BeNull();
        command.ToolVersionsValue.GitBranch.Should().BeNull();
    }

    // ── Regression guard: project-mode setup ordering is unchanged ──────────────────────────────

    [Fact]
    public async Task CheckSetupAsync_ProjectMode_StillChecksGitThenGitRepoBeforeDotnetAndPac()
    {
        Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), ".git")).Should().BeFalse();

        var command = MakeCommand();
        // Standalone left false (default) — project mode, unchanged base branch.
        var settings = new FlowlineSettings { NoCache = true };

        // Project mode must still fail at the git-repo check (git itself is installed, so that probe
        // succeeds first) — proving git and git-repo still run, and run before dotnet/pac, since
        // RuntimeOptions.ToolVersions is only assigned after all four checks complete.
        var act = () => command.RunCheckSetupAsync(settings, CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.Should().Match<FlowlineException>(e =>
                e.ExitCode == ExitCode.ConfigInvalid &&
                e.Message == "No Git repo found. Run 'git init' or 'git clone' first.");
        // Confirms dotnet/pac never ran either — ToolVersions is only assigned after all four checks.
        command.ToolVersionsValue.Should().BeNull();
    }
}
