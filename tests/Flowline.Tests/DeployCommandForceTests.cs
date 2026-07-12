using FluentAssertions;
using Flowline.Commands;

namespace Flowline.Tests;

public class DeployCommandForceTests
{
    [Fact]
    public void ValidateForce_UnrecognizedValue_ThrowsNamingValidValues()
    {
        var settings = new DeployCommand.Settings { Force = ["dirty"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, DeployCommand.ValidSpecifiers, "deploy");

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ValidationFailed
                && e.Message.Contains("drift") && e.Message.Contains("config") && e.Message.Contains("all"));
    }

    [Fact]
    public void ValidateForce_ValidValues_DoesNotThrow()
    {
        var settings = new DeployCommand.Settings { Force = ["drift"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, DeployCommand.ValidSpecifiers, "deploy");

        act.Should().NotThrow();
    }

    [Fact]
    public void HasForce_All_ApprovesDriftAndConfigTogether()
    {
        var settings = new DeployCommand.Settings { Force = ["all"] };

        settings.HasForce("drift").Should().BeTrue();
        settings.HasForce("config").Should().BeTrue();
    }

    [Fact]
    public void HasForce_DriftOnly_DoesNotApproveConfig()
    {
        var settings = new DeployCommand.Settings { Force = ["drift"] };

        settings.HasForce("drift").Should().BeTrue();
        settings.HasForce("config").Should().BeFalse();
    }
}
