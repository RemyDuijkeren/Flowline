using FluentAssertions;
using Flowline.Commands;

namespace Flowline.Tests;

public class CloneCommandTests
{
    [Fact]
    public void ValidateForce_UnrecognizedValue_ThrowsNamingConfigAndAll()
    {
        var settings = new CloneCommand.Settings { Force = ["dirty"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, FlowlineSettings.ConfigOnlyValidSpecifiers, "clone");

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ValidationFailed && e.Message.Contains("config") && e.Message.Contains("all"));
    }

    [Fact]
    public void ValidateForce_Config_DoesNotThrow()
    {
        var settings = new CloneCommand.Settings { Force = ["config"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, FlowlineSettings.ConfigOnlyValidSpecifiers, "clone");

        act.Should().NotThrow();
    }

    [Fact]
    public void HasForce_All_ApprovesConfig()
    {
        var settings = new CloneCommand.Settings { Force = ["all"] };

        settings.HasForce("config").Should().BeTrue();
    }
}
