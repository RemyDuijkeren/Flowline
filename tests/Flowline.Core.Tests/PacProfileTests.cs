using Flowline.Core.Models;
using FluentAssertions;

namespace Flowline.Core.Tests;

public class PacProfileTests
{
    [Fact]
    public void DisplayName_NameSet_ReturnsName()
    {
        var profile = new PacProfile { Name = "MyProfile" };

        profile.DisplayName.Should().Be("MyProfile");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DisplayName_NameEmptyOrNull_ReturnsUnnamed(string? name)
    {
        var profile = new PacProfile { Name = name };

        profile.DisplayName.Should().Be("unnamed");
    }

    [Fact]
    public void EnvironmentLabel_FriendlyNameSet_ReturnsFriendlyNameWithUrl()
    {
        var profile = new PacProfile
        {
            FriendlyName = "AutomateValue Dev",
            Resource = "https://automatevalue-dev.crm4.dynamics.com/"
        };

        profile.EnvironmentLabel.Should().Be("AutomateValue Dev (https://automatevalue-dev.crm4.dynamics.com/)");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EnvironmentLabel_NoFriendlyName_ReturnsUrlOnly(string? friendlyName)
    {
        var profile = new PacProfile
        {
            FriendlyName = friendlyName,
            Resource = "https://automatevalue-dev.crm4.dynamics.com/"
        };

        profile.EnvironmentLabel.Should().Be("https://automatevalue-dev.crm4.dynamics.com/");
    }

    [Fact]
    public void EnvironmentLabel_NoFriendlyNameOrResource_ReturnsEmpty()
    {
        var profile = new PacProfile();

        profile.EnvironmentLabel.Should().BeEmpty();
    }
}
