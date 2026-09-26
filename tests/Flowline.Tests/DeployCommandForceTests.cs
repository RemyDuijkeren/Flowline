using FluentAssertions;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Settings;

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
                && e.Message.Contains("drift") && e.Message.Contains("first-import") && e.Message.Contains("all"));
    }

    [Fact]
    public void ValidateForce_ValidValues_DoesNotThrow()
    {
        var settings = new DeployCommand.Settings { Force = ["drift"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, DeployCommand.ValidSpecifiers, "deploy");

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateForce_Config_ThrowsAsUnrecognized()
    {
        var settings = new DeployCommand.Settings { Force = ["config"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, DeployCommand.ValidSpecifiers, "deploy");

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ValidationFailed && e.Message.Contains("first-import"));
    }

    [Fact]
    public void HasForce_All_ApprovesDriftAndFirstImportTogether()
    {
        var settings = new DeployCommand.Settings { Force = ["all"] };
        var options = new FlowlineRuntimeOptions { Force = settings.Force };

        options.HasForce("drift").Should().BeTrue();
        options.HasForce("first-import").Should().BeTrue();
    }

    [Fact]
    public void HasForce_DriftOnly_DoesNotApproveFirstImport()
    {
        var settings = new DeployCommand.Settings { Force = ["drift"] };
        var options = new FlowlineRuntimeOptions { Force = settings.Force };

        options.HasForce("drift").Should().BeTrue();
        options.HasForce("first-import").Should().BeFalse();
    }
}
