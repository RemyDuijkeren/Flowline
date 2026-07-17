using FluentAssertions;
using Flowline.Commands;
using Flowline.Core;

namespace Flowline.Tests;

public class StatusCommandTests
{
    [Fact]
    public void ValidateForce_AnyValue_Throws()
    {
        var settings = new StatusCommand.Settings { Force = ["config"] };

        var act = () => StatusCommand.ValidateForce(settings);

        act.Should().Throw<FlowlineException>().Where(e => e.ExitCode == ExitCode.ValidationFailed);
    }

    [Fact]
    public void ValidateForce_Empty_DoesNotThrow()
    {
        var settings = new StatusCommand.Settings();

        var act = () => StatusCommand.ValidateForce(settings);

        act.Should().NotThrow();
    }
}
