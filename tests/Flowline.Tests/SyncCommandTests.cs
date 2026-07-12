using FluentAssertions;
using Flowline.Commands;

namespace Flowline.Tests;

public class SyncCommandTests
{
    [Fact]
    public void Settings_Force_ShouldDefaultToEmpty()
    {
        new SyncCommand.Settings().Force.Should().BeEmpty();
    }

    [Fact]
    public void ValidateForce_UnrecognizedValue_ThrowsNamingValidValues()
    {
        var settings = new SyncCommand.Settings { Force = ["delete-orphans"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, SyncCommand.ValidSpecifiers, "sync");

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ValidationFailed
                && e.Message.Contains("dirty") && e.Message.Contains("config") && e.Message.Contains("all"));
    }

    [Fact]
    public void ValidateForce_ValidValues_DoesNotThrow()
    {
        var settings = new SyncCommand.Settings { Force = ["dirty"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, SyncCommand.ValidSpecifiers, "sync");

        act.Should().NotThrow();
    }

    [Fact]
    public void HasForce_All_ApprovesDirtyAndConfigTogether()
    {
        var settings = new SyncCommand.Settings { Force = ["all"] };

        settings.HasForce("dirty").Should().BeTrue();
        settings.HasForce("config").Should().BeTrue();
    }

    [Fact]
    public void HasForce_ConfigOnly_DoesNotApproveDirty()
    {
        var settings = new SyncCommand.Settings { Force = ["config"] };

        settings.HasForce("config").Should().BeTrue();
        settings.HasForce("dirty").Should().BeFalse();
    }

    [Fact]
    public void Settings_Bump_ShouldDefaultToPatch()
    {
        new SyncCommand.Settings().Bump.Should().Be(BumpComponent.Patch);
    }
}

public class BumpVersionTests
{
    [Theory]
    [InlineData("1.0.0.1", BumpComponent.Patch, "1.0.1.0")]
    [InlineData("1.0.9.3", BumpComponent.Patch, "1.0.10.0")]
    [InlineData("1.2.5.3", BumpComponent.Minor, "1.3.0.0")]
    [InlineData("1.2.5.3", BumpComponent.Major, "2.0.0.0")]
    public void BumpVersion_ShouldIncrementCorrectComponent(string version, BumpComponent component, string expected)
    {
        SyncCommand.BumpVersion(version, component).Should().Be(expected);
    }

    [Fact]
    public void BumpVersion_None_ShouldThrow()
    {
        var act = () => SyncCommand.BumpVersion("1.0.0.1", BumpComponent.None);

        act.Should().Throw<ArgumentOutOfRangeException>();
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
