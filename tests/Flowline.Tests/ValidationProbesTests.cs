using FluentAssertions;
using Flowline.Core.Models;
using Flowline.Validation;

namespace Flowline.Tests;

public class ValidationProbesTests
{
    [Fact]
    public void MapToEnvironmentInfo_NullInput_ReturnsNull() =>
        ValidationProbes.MapToEnvironmentInfo(null).Should().BeNull();

    [Fact]
    public void MapToEnvironmentInfo_MapsAllFields()
    {
        var bapEnv = new BapEnvironmentInfo
        {
            EnvironmentId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            EnvironmentUrl = "https://contoso.crm4.dynamics.com/",
            OrganizationId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            DisplayName = "Contoso Dev",
            Type = "Sandbox",
            DomainName = "contoso",
            Version = "9.2.23092.00206"
        };

        var result = ValidationProbes.MapToEnvironmentInfo(bapEnv);

        result.Should().NotBeNull();
        result!.EnvironmentId.Should().Be(bapEnv.EnvironmentId);
        result.EnvironmentUrl.Should().Be(bapEnv.EnvironmentUrl);
        result.OrganizationId.Should().Be(bapEnv.OrganizationId);
        result.DisplayName.Should().Be(bapEnv.DisplayName);
        result.Type.Should().Be(bapEnv.Type);
        result.DomainName.Should().Be(bapEnv.DomainName);
        result.Version.Should().Be(bapEnv.Version);
    }
}
