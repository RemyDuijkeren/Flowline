using FluentAssertions;
using Flowline.Commands;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Core.OrphanCleanup;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flowline.Tests;

public class DriftCommandTests
{
    // ── Target → EnvironmentRole resolution: happy paths ─────────────────────

    [Theory]
    [InlineData("prod", EnvironmentRole.Prod)]
    [InlineData("uat", EnvironmentRole.Uat)]
    [InlineData("test", EnvironmentRole.Test)]
    [InlineData("dev", EnvironmentRole.Dev)]
    [InlineData("PROD", EnvironmentRole.Prod)]
    [InlineData("Dev", EnvironmentRole.Dev)]
    public void TryResolveRole_MapsKeywordToRole(string target, EnvironmentRole expected)
    {
        DriftCommand.TryResolveRole(target).Should().Be(expected);
    }

    // ── Target → EnvironmentRole resolution: not a role keyword ───────────────

    [Theory]
    [InlineData("staging")]
    [InlineData("https://contoso-test.crm4.dynamics.com/")]
    public void TryResolveRole_ReturnsNull_WhenTargetIsNotARoleKeyword(string target)
    {
        // Anything that isn't one of the four role keywords is treated as a literal URL by the
        // caller (ResolveEnvironmentAsync) — TryResolveRole itself just signals "not a role."
        DriftCommand.TryResolveRole(target).Should().BeNull();
    }

    // ── Exit code selection ───────────────────────────────────────────────────

    [Fact]
    public void SelectExitCode_ReturnsSuccess_WhenComparedAndNoDriftEntries()
    {
        var result = new CompareResult([]);

        DriftCommand.SelectExitCode(result).Should().Be((int)ExitCode.Success);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void SelectExitCode_ReturnsValidationFailed_WhenDriftEntriesFound(int entryCount)
    {
        var entries = Enumerable.Range(0, entryCount)
            .Select(_ => new OrphanEntry(Guid.NewGuid(), 91, "SomeComponent", OrphanAction.Delete))
            .ToList();
        var result = new CompareResult(entries);

        DriftCommand.SelectExitCode(result).Should().Be((int)ExitCode.ValidationFailed);
    }

    [Fact]
    public void SelectExitCode_ReturnsInconclusive_WhenComparisonWasSkipped()
    {
        var result = new CompareResult([], Skipped: true);

        DriftCommand.SelectExitCode(result).Should().Be((int)ExitCode.Inconclusive);
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

    // ── R15: role keyword in standalone — reworded, no ".flowline" mention ───────────────────────────

    [Theory]
    [InlineData("prod")]
    [InlineData("uat")]
    [InlineData("test")]
    [InlineData("dev")]
    public void BuildStandaloneRoleError_NamesTargetAndOmitsFlowlineConfig(string target)
    {
        var message = DriftCommand.BuildStandaloneRoleError(target);

        message.Should().Contain(target);
        message.Should().NotContain(".flowline");
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
}
