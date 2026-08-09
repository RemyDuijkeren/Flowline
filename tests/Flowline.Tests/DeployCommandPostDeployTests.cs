using FluentAssertions;
using Flowline.Commands;
using Flowline.Services;

namespace Flowline.Tests;

public class DeployCommandPostDeployTests
{
    [Theory]
    [InlineData(0, false)]     // no failures
    [InlineData(1, true)]      // single service reports a failure
    [InlineData(5, true)]      // multiple services' failures summed by the caller
    public void ShouldReportPartialSuccess_ReturnsExpected(int cleanupFailures, bool expected) =>
        DeployCommand.ShouldReportPartialSuccess(cleanupFailures).Should().Be(expected);

    [Theory]
    [InlineData(0, false)]     // no Critical findings — deploy proceeds
    [InlineData(1, true)]      // single Critical finding aborts the gate
    public void ShouldAbort_ReturnsExpected(int criticalCount, bool expected) =>
        SolutionCheckService.ShouldAbort(criticalCount).Should().Be(expected);

    [Fact]
    public void SkipComponentCheck_DefaultsToFalse() =>
        new DeployCommand.Settings().SkipComponentCheck.Should().BeFalse();

    [Fact]
    public void SkipComponentCheck_SetIndependentlyOfTheOtherSkipFlags()
    {
        var settings = new DeployCommand.Settings { SkipComponentCheck = true };

        settings.SkipComponentCheck.Should().BeTrue();
        settings.SkipSolutionCheck.Should().BeFalse();
        settings.NoBackup.Should().BeFalse();
    }
}
