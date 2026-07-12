using FluentAssertions;

namespace Flowline.Tests;

public class FlowlineSettingsTests
{
    [Fact]
    public void HasForce_IsCaseInsensitive_ForSpecifierValue()
    {
        var settings = new FlowlineSettings { Force = ["CONFIG"] };

        settings.HasForce("config").Should().BeTrue();
    }

    [Fact]
    public void HasForce_IsCaseInsensitive_ForAllValue()
    {
        var settings = new FlowlineSettings { Force = ["ALL"] };

        settings.HasForce("config").Should().BeTrue();
    }

    [Fact]
    public void ValidateForce_IsCaseInsensitive_AcceptsMixedCaseValidValue()
    {
        var act = () => FlowlineSettings.ValidateForce(["Config"], ["config", "all"], "clone");

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateForce_EmptyForce_DoesNotThrow()
    {
        var act = () => FlowlineSettings.ValidateForce([], ["config", "all"], "clone");

        act.Should().NotThrow();
    }
}
