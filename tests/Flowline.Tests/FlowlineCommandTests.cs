using Flowline.Commands;
using FluentAssertions;

namespace Flowline.Tests;

public class FlowlineCommandTests
{
    [Theory]
    [InlineData(0,  "0s")]
    [InlineData(1,  "1s")]
    [InlineData(59, "59s")]
    public void FormatDuration_UnderOneMinute_ShowsSeconds(int seconds, string expected)
    {
        FlowlineCommand<FlowlineSettings>.FormatDuration(TimeSpan.FromSeconds(seconds))
            .Should().Be(expected);
    }

    [Fact]
    public void PackageFolder_ReturnsSlnFolderWithPackageSubfolder()
    {
        var slnFolder = Path.Combine("solutions", "MySolution");
        FlowlineCommand<FlowlineSettings>.PackageFolder(slnFolder)
            .Should().Be(Path.Combine("solutions", "MySolution", "Package"));
    }

    [Theory]
    [InlineData(60,  "1m 0s")]
    [InlineData(61,  "1m 1s")]
    [InlineData(90,  "1m 30s")]
    [InlineData(120, "2m 0s")]
    [InlineData(125, "2m 5s")]
    public void FormatDuration_OneMinuteOrMore_ShowsMinutesAndSeconds(int seconds, string expected)
    {
        FlowlineCommand<FlowlineSettings>.FormatDuration(TimeSpan.FromSeconds(seconds))
            .Should().Be(expected);
    }
}
