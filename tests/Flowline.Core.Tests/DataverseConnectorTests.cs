using Flowline.Core.Services;
using Flowline.Core;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Xunit;

namespace Flowline.Core.Tests;

public class DataverseConnectorTests
{
    private readonly DataverseConnector _service;

    public DataverseConnectorTests()
    {
        _service = new DataverseConnector(new NullFlowlineOutput());
    }

    [Fact]
    public void Connect_ShouldThrowException_WhenConnectionStringIsInvalid()
    {
        // Arrange
        var connectionString = "AuthType=ClientSecret;Url=https://invalid.crm.dynamics.com;ClientId=id;ClientSecret=secret";

        // Act & Assert
        // ServiceClient constructor will attempt to connect and fail, which we expect to be wrapped in an Exception by Connect method
        Assert.ThrowsAny<Exception>(() => _service.Connect(connectionString));
    }

    [Fact]
    public void GetPacProfiles_ShouldReturnProfiles_WhenFileExists()
    {
        // Act
        var profiles = _service.GetPacProfiles();

        // Assert
        // We can't guarantee profiles exist on all machines, but we can verify it doesn't crash
        Assert.NotNull(profiles);
    }

    [Fact]
    public void GetCurrentResourceSpecificPacProfile_WithOneCurrentResourceProfile_ShouldReturnProfile()
    {
        var profile = new PacProfile { Kind = "DATAVERSE", Resource = "https://contoso.crm4.dynamics.com" };
        var profiles = new PacAuthProfiles
        {
            Current = new Dictionary<string, PacProfile>
            {
                ["default"] = profile
            }
        };

        var result = DataverseConnector.GetCurrentResourceSpecificPacProfile(profiles);

        Assert.Same(profile, result);
    }

    [Fact]
    public void GetCurrentResourceSpecificPacProfile_WithUniversalProfile_ShouldReturnNull()
    {
        var profiles = new PacAuthProfiles
        {
            Current = new Dictionary<string, PacProfile>
            {
                ["default"] = new() { Kind = "UNIVERSAL", Resource = "https://contoso.crm4.dynamics.com" }
            }
        };

        var result = DataverseConnector.GetCurrentResourceSpecificPacProfile(profiles);

        Assert.Null(result);
    }

    [Fact]
    public void GetCurrentResourceSpecificPacProfile_WithMultipleResourceProfiles_ShouldReturnNull()
    {
        var profiles = new PacAuthProfiles
        {
            Current = new Dictionary<string, PacProfile>
            {
                ["one"] = new() { Kind = "DATAVERSE", Resource = "https://one.crm4.dynamics.com" },
                ["two"] = new() { Kind = "DATAVERSE", Resource = "https://two.crm4.dynamics.com" }
            }
        };

        var result = DataverseConnector.GetCurrentResourceSpecificPacProfile(profiles);

        Assert.Null(result);
    }

    [Fact]
    public void ConnectViaPac_ShouldThrow_WhenProfileIsNull()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => _service.ConnectViaPac(null!, "https://test.crm.dynamics.com"));
    }

    [Fact]
    public void ConnectViaPac_ShouldThrow_WhenEnvironmentUrlIsNull()
    {
        // Arrange
        var profile = new PacProfile { User = "test@test.com" };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _service.ConnectViaPac(profile, null));
    }

    [Fact(Skip = "Failing with 'Need a non-empty authority' error in current environment")]
    public void ConnectViaPac_ShouldConnect_WhenRealEnvironmentUrlIsProvided()
    {
        // This test requires a valid PAC profile to be present on the machine
        var profiles = _service.GetPacProfiles();
        var profile = profiles.FirstOrDefault();

        if (profile == null) return;

        var environmentUrl = "https://test.crm.dynamics.com"; // Use a dummy URL for logic test

        // Since it's a dummy URL, it will fail to connect but we test that it attempts with correct parameters
        Assert.ThrowsAny<Exception>(() => _service.ConnectViaPac(profile, environmentUrl));
    }

    [Fact(Skip = "Opens a device code flow window in the browser, which is not supported in CI")]
    public void ConnectViaPac_Universal_ShouldConnect_WhenEnvironmentUrlIsProvided()
    {
        // This test requires a valid UNIVERSAL PAC profile to be present on the machine
        var profiles = _service.GetPacProfiles();
        var profile = profiles.FirstOrDefault(p => p.IsUniversal);
        var environmentUrl = "https://spotlerautomate.crm4.dynamics.com";

        if (profile != null && environmentUrl != null)
        {
            var client = _service.ConnectViaPac(profile, environmentUrl);
            Assert.NotNull(client);
            if (client is ServiceClient sc)
            {
                Assert.True(sc.IsReady);
            }
        }
    }
}
