using FluentAssertions;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Core.Services.OrphanCleanup;

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
}
