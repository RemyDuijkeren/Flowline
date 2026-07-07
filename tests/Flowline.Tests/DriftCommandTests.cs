using FluentAssertions;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Core.Services;

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
    public void ResolveRole_MapsKeywordToRole(string target, EnvironmentRole expected)
    {
        DriftCommand.ResolveRole(target).Should().Be(expected);
    }

    // ── Target → EnvironmentRole resolution: edge case ────────────────────────

    [Fact]
    public void ResolveRole_Throws_WhenTargetIsUnknown()
    {
        var act = () => DriftCommand.ResolveRole("staging");

        act.Should().Throw<FlowlineException>()
            .Which.Message.Should().ContainAll("prod", "uat", "test", "dev");
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
}
