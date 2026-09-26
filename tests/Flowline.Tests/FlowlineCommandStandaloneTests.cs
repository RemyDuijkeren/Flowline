using Flowline;
using Flowline.Core;
using Flowline.Core.Dataverse;
using Flowline.Core.Environments;
using Flowline.Core.Updates;
using Flowline.Core.Validation;
using Flowline.Diagnostics;
using Flowline.Logging;
using Flowline.Services;
using Flowline.Settings;
using Flowline.Tests;
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

    sealed class TestCommand(CommandServices services)
        : FlowlineCommand<FlowlineSettings>(services)
    {
        public bool Standalone { get; set; }
        public bool RequiresFlowlineProjectValue { get; set; } = true;
        public string[] ForceSpecifiers { get; set; } = [];

        // Isolates RootFolder resolution (and force validation) from the setup check — scenarios that
        // only care about which branch RootFolder takes must not also run it.
        public bool SkipSetup { get; set; }

        protected override bool IsStandalone(FlowlineSettings settings) => Standalone;
        protected override bool RequiresFlowlineProject => RequiresFlowlineProjectValue;
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

    // Fixed versions for every tool probe, each call recorded, so setup assertions don't depend on what
    // the test machine has installed.
    static ValidationProbes StubProbes(List<string> probed) => new()
    {
        CheckPacAsync = (_, _) => { probed.Add("pac"); return Task.FromResult(("2.12.2", "Dotnet Tool (.NET)")); },
        CheckDotNetAsync = (_, _) => { probed.Add("dotnet"); return Task.FromResult("10.0.401"); },
        CheckGitAsync = (_, _) => { probed.Add("git"); return Task.FromResult("2.55.0"); },
        CheckGitRepoAsync = (_, _, _) => { probed.Add("git-repo"); return Task.CompletedTask; },
    };

    static TestCommand MakeCommand(ValidationProbes? probes = null)
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = false;
        var connector = new DataverseConnector(console, new HttpClient());
        var profileResolutionService = new ProfileResolutionService(console, connector, new FlowlineRuntimeOptions());
        return new TestCommand(new CommandServices(console, new FlowlineRuntimeOptions(), profileResolutionService,
            NullLoggerFactory.Instance, new SubprocessCapture(console), new NuGetVersionClient(new HttpClient()), TestValidator.Create(probes ?? StubProbes([]))));
    }

    static CommandContext MakeContext(string name = "test-command") =>
        new(Array.Empty<string>(), new NoRemainingArguments(), name, null);

    // ── R3 / KTD1: RootFolder resolution consults IsStandalone OR !RequiresFlowlineProject ──────────────

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
    public async Task ExecuteAsync_RequiresFlowlineProjectFalseAndPredicateFalse_StillResolvesToWorkingDirectory()
    {
        // Regression guard for CloneCommand/InitCommand/SlnAddCommand/ScaffoldCommand: none of them set
        // the new standalone predicate — they rely solely on the pre-existing RequiresFlowlineProject => false.
        var command = MakeCommand();
        command.RequiresFlowlineProjectValue = false;
        command.SkipSetup = true;
        var settings = new FlowlineSettings();

        var act = () => command.RunAsync(MakeContext(), settings, CancellationToken.None);

        await act.Should().NotThrowAsync();
        command.ResolvedRootFolder.Should().Be(Directory.GetCurrentDirectory());
    }

    // ── KTD6: the scrubber learns the run's known values whether or not the invocation is logged ──

    [Fact]
    public async Task ExecuteAsync_RegistersTheProjectFolderNameWithTheScrubber_EvenWhenNothingIsLogged()
    {
        // SkipSetup leaves ToolVersions null, which is the case InvocationLogger returns early on. The
        // registration must not ride along with it: a command that skips the tool probes still puts the
        // project folder name in every log line it writes. RootFolder falls back to the working
        // directory here, so that folder's name is the value under test — nothing changes the process
        // working directory, which other tests in this assembly read.
        FlowlineScrubber.Initialize("test-salt"u8.ToArray());
        try
        {
            var command = MakeCommand();
            command.Standalone = true;
            command.SkipSetup = true;

            await command.RunAsync(MakeContext(), new FlowlineSettings(), CancellationToken.None);

            var folderName = Path.GetFileName(
                Directory.GetCurrentDirectory().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            FlowlineScrubber.Current.Scrub($"root={folderName}").Should().NotContain(folderName);
        }
        finally
        {
            FlowlineScrubber.Initialize([]);
        }
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
        var probed = new List<string>();
        var command = MakeCommand(StubProbes(probed));
        command.Standalone = true;
        var settings = new FlowlineSettings();

        await command.RunCheckSetupAsync(settings, CancellationToken.None);

        probed.Should().Equal("pac");

        // Telemetry: standalone fills ToolVersions from the one tool it probes, so InvocationLogger
        // clears its null guard. Dotnet and git stay null because standalone never checks them —
        // "not checked", not a placeholder that would read as a real version downstream.
        command.ToolVersionsValue.Should().NotBeNull();
        command.ToolVersionsValue!.PacVersion.Should().Be("2.12.2");
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

    // ── Regression guard: project mode still probes the tools standalone skips ──────────────────

    [Fact]
    public async Task CheckSetupAsync_ProjectMode_ProbesGitAndDotnet_UnlikeStandalone()
    {
        var probed = new List<string>();
        var command = MakeCommand(StubProbes(probed));
        var settings = new FlowlineSettings();

        await command.RunCheckSetupAsync(settings, CancellationToken.None);

        probed.Should().Equal("git", "git-repo", "dotnet", "pac");
        command.ToolVersionsValue.Should().NotBeNull();
        command.ToolVersionsValue!.GitVersion.Should().Be("2.55.0");
        command.ToolVersionsValue.DotNetVersion.Should().Be("10.0.401");
        command.ToolVersionsValue.PacVersion.Should().Be("2.12.2");
    }
}
