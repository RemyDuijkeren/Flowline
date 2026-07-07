using FluentAssertions;
using Flowline.Commands;
using Flowline.Core;

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
    public void SelectExitCode_ReturnsSuccess_WhenNoDriftEntries()
    {
        DriftCommand.SelectExitCode(0).Should().Be((int)ExitCode.Success);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void SelectExitCode_ReturnsValidationFailed_WhenDriftEntriesFound(int entryCount)
    {
        DriftCommand.SelectExitCode(entryCount).Should().Be((int)ExitCode.ValidationFailed);
    }
}
