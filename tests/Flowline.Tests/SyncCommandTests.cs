using FluentAssertions;
using Flowline.Commands;
using Flowline.Core;

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

/// <summary>
/// Sync's output shape — where CHANGES.md lands, its provenance line, and the terminal no-changes line —
/// is composed here and passed to a writer shared with `diff`. The call site that uses it needs a live
/// environment to reach, so these lock the values directly.
/// </summary>
public class SyncCommandOutputShapeTests
{
    [Fact]
    public void ChangesFilePath_IsChangesMdAtTheGivenRoot()
    {
        SyncCommand.ChangesFilePath(Path.Combine("C:", "repo"))
            .Should().Be(Path.Combine("C:", "repo", "CHANGES.md"));
    }

    [Fact]
    public void ProvenanceLine_NamesTheEnvironment()
    {
        SyncCommand.ProvenanceLine("Contoso Dev").Should().Be("Synced from: Contoso Dev");
    }

    [Fact]
    public void ProvenanceLine_WithoutEnvironment_IsOmittedEntirely()
    {
        SyncCommand.ProvenanceLine(null).Should().BeNull();
    }

    [Fact]
    public void NoChangesLine_NamesTheEnvironment()
    {
        SyncCommand.NoChangesLine("Contoso Dev").Should().Be("No changes pulled from Contoso Dev.");
    }

    [Fact]
    public void NoChangesLine_WithoutEnvironment_FallsBackToDev()
    {
        SyncCommand.NoChangesLine(null).Should().Be("No changes pulled from DEV.");
    }

    [Fact]
    public void NoChangesLine_EscapesMarkupInTheEnvironmentName()
    {
        // An environment display name is user data reaching a Spectre markup renderer.
        SyncCommand.NoChangesLine("Contoso [Dev]").Should().Be("No changes pulled from Contoso [[Dev]].");
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
