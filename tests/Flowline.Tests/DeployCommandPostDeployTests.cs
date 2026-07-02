using FluentAssertions;
using Flowline.Commands;

namespace Flowline.Tests;

public class DeployCommandPostDeployTests
{
    [Theory]
    [InlineData(0, false)]     // no failures
    [InlineData(1, true)]      // single service reports a failure
    [InlineData(5, true)]      // multiple services' failures summed by the caller
    public void ShouldReportPartialSuccess_ReturnsExpected(int cleanupFailures, bool expected) =>
        DeployCommand.ShouldReportPartialSuccess(cleanupFailures).Should().Be(expected);
}
