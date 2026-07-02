using FluentAssertions;
using Flowline.Commands;

namespace Flowline.Tests;

public class DeployCommandPostDeployTests
{
    [Fact]
    public void ShouldReportPartialSuccess_ZeroFailures_ReturnsFalse()
    {
        DeployCommand.ShouldReportPartialSuccess(0).Should().BeFalse();
    }

    [Fact]
    public void ShouldReportPartialSuccess_SingleServiceFailure_ReturnsTrue()
    {
        DeployCommand.ShouldReportPartialSuccess(1).Should().BeTrue();
    }

    [Fact]
    public void ShouldReportPartialSuccess_MultipleServicesSummedFailures_ReturnsTrue()
    {
        // Simulates two IPostDeployService implementers each reporting failures, summed by the caller.
        var aggregated = 2 + 3;

        DeployCommand.ShouldReportPartialSuccess(aggregated).Should().BeTrue();
    }
}
