using Flowline;
using FluentAssertions;

namespace Flowline.Tests;

public class TryCountSeveritiesTests
{
    const string CleanTable =
        "Checker results:\r\n" +
        "    Files: 20220602060955_emptySolution0100.zip\r\n" +
        "    Correlation Id: 4bf16222-46f7-47f0-a419-a5a1a59d96c3\r\n" +
        "    Status: Finished\r\n" +
        "\r\n" +
        "\r\n" +
        "Critical High Medium  Low Informational\r\n" +
        "\r\n" +
        "    0    0       0   0             0\r\n";

    const string OneCriticalTwoMediumTable =
        "Checker results:\r\n" +
        "    Status: Finished\r\n" +
        "\r\n" +
        "Critical High Medium  Low Informational\r\n" +
        "\r\n" +
        "    1    0       2   0             0\r\n";

    [Fact]
    public void TryCountSeverities_ReturnsCriticalAndTotal_WhenTableHasFindings()
    {
        var result = PacUtils.TryCountSeverities(OneCriticalTwoMediumTable, out var critical, out var total);

        result.Should().BeTrue();
        critical.Should().Be(1);
        total.Should().Be(3);
    }

    [Fact]
    public void TryCountSeverities_ReturnsZeroCounts_WhenReportIsClean()
    {
        var result = PacUtils.TryCountSeverities(CleanTable, out var critical, out var total);

        result.Should().BeTrue();
        critical.Should().Be(0);
        total.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Checker results:\r\n    Status: Finished\r\n")]
    [InlineData("garbage output with no summary table at all")]
    [InlineData("Critical High Medium  Low Informational\r\n")]
    [InlineData("Critical High Medium  Low Informational\r\n\r\n    x    0       2   0             0\r\n")]
    [InlineData("Critical High Medium  Low Informational\r\n\r\n    0    0\r\n")]
    public void TryCountSeverities_ReturnsFalse_WhenTableIsMissingOrMalformed(string output)
    {
        var result = PacUtils.TryCountSeverities(output, out var critical, out var total);

        result.Should().BeFalse();
        critical.Should().Be(0);
        total.Should().Be(0);
    }
}

public class BuildCheckResultTests
{
    const string OutputDirectory = "C:\\artifacts\\checker-output";

    [Fact]
    public void BuildCheckResult_Throws_WhenPacExitCodeIsNonZero()
    {
        Action act = () => PacUtils.BuildCheckResult(1, "", OutputDirectory);

        act.Should().Throw<FlowlineException>()
            .Where(ex => ex.ExitCode == ExitCode.ConnectionFailed);
    }

    [Fact]
    public void BuildCheckResult_ReturnsParsedCounts_WhenPacSucceedsAndTableParses()
    {
        var result = PacUtils.BuildCheckResult(0,
            "Critical High Medium  Low Informational\r\n\r\n    1    0       2   0             0\r\n",
            OutputDirectory);

        result.CriticalCount.Should().Be(1);
        result.TotalCount.Should().Be(3);
        result.OutputDirectory.Should().Be(OutputDirectory);
    }

    [Fact]
    public void BuildCheckResult_Throws_WhenPacSucceedsButTableIsUnparsable()
    {
        Action act = () => PacUtils.BuildCheckResult(0, "no summary table here", OutputDirectory);

        act.Should().Throw<FlowlineException>()
            .Where(ex => ex.ExitCode == ExitCode.GeneralError);
    }
}
