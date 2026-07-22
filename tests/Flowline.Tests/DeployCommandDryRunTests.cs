using FluentAssertions;
using Flowline.Commands;
using Flowline.Core.Models;

namespace Flowline.Tests;

public class DeployCommandDryRunTests
{
    [Theory]
    [InlineData(true, false, false, RunMode.DryRun)]   // dry-run alone
    [InlineData(true, true, true, RunMode.DryRun)]     // dry-run takes precedence over noDelete and managed
    [InlineData(false, true, false, RunMode.NoDelete)] // existing behavior: --no-delete unchanged
    [InlineData(false, false, true, RunMode.NoDelete)] // existing behavior: managed unchanged
    [InlineData(false, false, false, RunMode.Normal)]  // existing behavior: plain deploy unchanged
    public void ResolveRunMode_ReturnsExpected(bool dryRun, bool noDelete, bool includeManaged, RunMode expected) =>
        DeployCommand.ResolveRunMode(dryRun, noDelete, includeManaged).Should().Be(expected);

    [Fact]
    public void BuildDryRunCompleteMessage_NamesSolutionAndTarget()
    {
        var message = DeployCommand.BuildDryRunCompleteMessage("ContosoSales", "Production");

        message.Should().Contain("ContosoSales");
        message.Should().Contain("Production");
        message.Should().Contain("--dry-run");
    }
}
