using Flowline;
using Flowline.Commands;
using FluentAssertions;

namespace Flowline.Tests;

public class FlowlineCommandTests
{
    [Theory]
    [InlineData(  0, "0ms")]
    [InlineData(500, "500ms")]
    [InlineData(999, "999ms")]
    public void FormatDuration_UnderOneSecond_ShowsMilliseconds(int ms, string expected)
    {
        FlowlineCommand<FlowlineSettings>.FormatDuration(TimeSpan.FromMilliseconds(ms))
            .Should().Be(expected);
    }

    [Theory]
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

    [Fact]
    public void RedactSensitiveArgs_ClientSecret_ReplacesWithAsterisks()
    {
        SubprocessCapture.RedactSensitiveArgs("--client-secret abc123")
            .Should().Contain("***");
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

    [Fact]
    public void FindProjectRoot_WhenConfigInStartDir_ReturnsStartDir()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(root, ".flowline"), "{}");

        var result = FlowlineCommand<FlowlineSettings>.FindProjectRoot(root);

        result.Should().Be(root);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void FindProjectRoot_WhenConfigInParentDir_ReturnsParent()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(root, ".flowline"), "{}");
        var sub = Directory.CreateDirectory(Path.Combine(root, "deep", "nested")).FullName;

        var result = FlowlineCommand<FlowlineSettings>.FindProjectRoot(sub);

        result.Should().Be(root);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void FindProjectRoot_WhenNoConfigAnywhere_ReturnsNull()
    {
        var isolated = Directory.CreateTempSubdirectory().FullName;

        var result = FlowlineCommand<FlowlineSettings>.FindProjectRoot(isolated);

        result.Should().BeNull();
        Directory.Delete(isolated, recursive: true);
    }
}
