using FluentAssertions;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Core.Config;
using Flowline.Core.Dataverse;
using Flowline.Core.Environments;
using Flowline.Core.Updates;
using Flowline.Diagnostics;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Flowline.Core.OrphanCleanup;
using Flowline.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flowline.Tests;

public class DriftCommandTests
{
    // ── Exit code selection ───────────────────────────────────────────────────

    // KD8: drift is read-only, so a completed comparison exits 0 whether or not orphans were found —
    // --exit-code is what turns "orphans found" into ChangesFound (22). These replace the old
    // ValidationFailed(15)-on-findings contract.

    [Fact]
    public void SelectExitCode_ReturnsSuccess_WhenComparedAndNoDriftEntries()
    {
        var result = new CompareResult([]);

        DriftCommand.SelectExitCode(result, exitCodeOnChanges: false).Should().Be((int)ExitCode.Success);
        DriftCommand.SelectExitCode(result, exitCodeOnChanges: true).Should().Be((int)ExitCode.Success);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void SelectExitCode_ReturnsSuccess_WhenDriftEntriesFoundWithoutExitCodeFlag(int entryCount)
    {
        var entries = Enumerable.Range(0, entryCount)
            .Select(_ => new OrphanEntry(Guid.NewGuid(), 91, "SomeComponent", OrphanAction.Delete))
            .ToList();
        var result = new CompareResult(entries);

        DriftCommand.SelectExitCode(result, exitCodeOnChanges: false).Should().Be((int)ExitCode.Success);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void SelectExitCode_ReturnsChangesFound_WhenDriftEntriesFoundWithExitCodeFlag(int entryCount)
    {
        var entries = Enumerable.Range(0, entryCount)
            .Select(_ => new OrphanEntry(Guid.NewGuid(), 91, "SomeComponent", OrphanAction.Delete))
            .ToList();
        var result = new CompareResult(entries);

        DriftCommand.SelectExitCode(result, exitCodeOnChanges: true).Should().Be((int)ExitCode.ChangesFound);
    }

    [Fact]
    public void SelectExitCode_ReturnsInconclusive_WhenComparisonWasSkipped()
    {
        var result = new CompareResult([], Skipped: true);

        DriftCommand.SelectExitCode(result, exitCodeOnChanges: false).Should().Be((int)ExitCode.Inconclusive);
        DriftCommand.SelectExitCode(result, exitCodeOnChanges: true).Should().Be((int)ExitCode.Inconclusive);
    }

    [Fact]
    public void ValidateForce_UnrecognizedValue_ThrowsNamingConfigAndAll()
    {
        var settings = new DriftCommand.Settings { Force = ["drift"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, FlowlineSettings.ConfigOnlyValidSpecifiers, "drift");

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ValidationFailed && e.Message.Contains("config") && e.Message.Contains("all"));
    }

    [Fact]
    public void ValidateForce_Config_DoesNotThrow()
    {
        var settings = new DriftCommand.Settings { Force = ["config"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, FlowlineSettings.ConfigOnlyValidSpecifiers, "drift");

        act.Should().NotThrow();
    }

    // ── U4: standalone predicate — DriftCommand.IsStandalone (protected override) delegates verbatim to
    // this same helper (KTD4: no second copy of "am I standalone" for deploy and drift to drift apart on).
    // ResolveStandaloneSolution/BuildStandaloneIdentityNote's own correctness is covered by
    // DeployCommandStandaloneTests — these three prove the specific rule DriftCommand's override applies. ────

    [Fact]
    public void ResolveStandalone_PathSetAndNoProject_ReturnsTrue_ForDriftToo()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);

        try
        {
            DeployCommand.ResolveStandalone("artifact.zip", dir).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveStandalone_PathSetButProjectFound_ReturnsFalse_ForDriftToo()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ProjectConfig.s_configFileName), "{}");

        try
        {
            DeployCommand.ResolveStandalone("artifact.zip", dir).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveStandalone_NoPath_ReturnsFalse_ForDriftToo()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);

        try
        {
            DeployCommand.ResolveStandalone(null, dir).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── R13: temp unpack dir is removed after the run, success or failure ────────────────────────────

    [Fact]
    public async Task RunInTempDirAsync_ActionSucceeds_RemovesTempDirAndReturnsResult()
    {
        var dir = Directory.CreateTempSubdirectory("flowline-drift-test-").FullName;
        File.WriteAllText(Path.Combine(dir, "marker.txt"), "x");

        var result = await DriftCommand.RunInTempDirAsync(dir, () => Task.FromResult(42), NullLogger.Instance);

        result.Should().Be(42);
        Directory.Exists(dir).Should().BeFalse();
    }

    [Fact]
    public async Task RunInTempDirAsync_ActionThrows_StillRemovesTempDirAndRethrows()
    {
        var dir = Directory.CreateTempSubdirectory("flowline-drift-test-").FullName;
        Func<Task<int>> failingAction = () => throw new InvalidOperationException("boom");

        var act = async () => await DriftCommand.RunInTempDirAsync(dir, failingAction, NullLogger.Instance);

        await act.Should().ThrowAsync<InvalidOperationException>();
        Directory.Exists(dir).Should().BeFalse();
    }

    // ── A target that is neither a role nor a URL fails first ────────────────────────────────────────

    sealed class DriftProbe(CommandServices services)
        : DriftCommand(services, new DataverseConnector(services.Console, new HttpClient()),
            new OrphanCleanupService(services.Console, []))
    {
        public Task<int> Run(Settings settings) =>
            ExecuteFlowlineAsync(new CommandContext(["drift", settings.Target], new NoArgs(), "drift", null), settings,
                CancellationToken.None);

        sealed class NoArgs : IRemainingArguments
        {
            public ILookup<string, string?> Parsed => Array.Empty<string>().ToLookup(x => x, x => (string?)x);
            public IReadOnlyList<string> Raw => [];
        }
    }

    static DriftProbe MakeDriftProbe()
    {
        var console = new TestConsole();
        var connector = new DataverseConnector(console, new HttpClient());
        return new DriftProbe(new CommandServices(console, new FlowlineRuntimeOptions(),
            new ProfileResolutionService(console, connector, new FlowlineRuntimeOptions()),
            NullLoggerFactory.Instance, new SubprocessCapture(console), new NuGetVersionClient(new HttpClient()), TestValidator.Create()));
    }

    // Used to reach the PAC profile lookup as a URL and come back as exit 4 with advice to authenticate.
    [Fact]
    public async Task ATargetThatIsNotARoleOrUrl_ExitsValidationFailedWithTheDriftLine()
    {
        var act = () => MakeDriftProbe().Run(new DriftCommand.Settings { Target = "staging" });

        (await act.Should().ThrowAsync<FlowlineException>())
            .Where(e => e.ExitCode == ExitCode.ValidationFailed)
            .Which.Message.Should().Contain("'staging'").And.Contain("flowline drift <dev|test|uat|prod|url>");
    }

    // The target is diagnosed before the artifact, so a bad target and a missing zip report the target.
    [Fact]
    public async Task ABadTargetWithAMissingArtifact_ReportsTheTargetFirst()
    {
        var act = () => MakeDriftProbe().Run(new DriftCommand.Settings { Target = "staging", Path = "missing.zip" });

        (await act.Should().ThrowAsync<FlowlineException>())
            .Where(e => e.ExitCode == ExitCode.ValidationFailed)
            .Which.Message.Should().Contain("'staging'");
    }
}
