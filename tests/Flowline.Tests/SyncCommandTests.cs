using FluentAssertions;
using Flowline.Commands;

namespace Flowline.Tests;

public class SyncCommandTests
{
    [Fact]
    public void Settings_Force_ShouldDefaultToFalse()
    {
        new SyncCommand.Settings().Force.Should().BeFalse();
    }

    [Fact]
    public void Settings_NoTag_ShouldDefaultToFalse()
    {
        new SyncCommand.Settings().NoTag.Should().BeFalse();
    }

    [Fact]
    public void Settings_Bump_ShouldDefaultToNull()
    {
        new SyncCommand.Settings().Bump.Should().BeNull();
    }
}

public class BumpVersionTests
{
    [Theory]
    [InlineData("1.0.0.1", "patch", "1.0.1.0")]
    [InlineData("1.0.9.3", "patch", "1.0.10.0")]
    [InlineData("1.2.5.3", "minor", "1.3.0.0")]
    [InlineData("1.2.5.3", "major", "2.0.0.0")]
    public void BumpVersion_ShouldIncrementCorrectComponent(string version, string component, string expected)
    {
        SyncCommand.BumpVersion(version, component).Should().Be(expected);
    }

    [Theory]
    [InlineData("1.0.1.0", "1.0.1")]
    [InlineData("1.0.1", "1.0.1")]
    [InlineData("2.0.0.0", "2.0.0")]
    [InlineData("1.3.0.0", "1.3.0")]
    public void ToTagVersion_ShouldReturnThreePart(string version, string expected)
    {
        SyncCommand.ToTagVersion(version).Should().Be(expected);
    }
}
