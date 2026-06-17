using Flowline;
using Flowline.Core;
using Flowline.Core.Services;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Spectre.Console.Testing;
using Xunit;

namespace Flowline.Core.Tests;

public class DataverseConnectorTests
{
    private readonly DataverseConnector _service;

    public DataverseConnectorTests()
    {
        _service = new DataverseConnector(new TestConsole(), new FlowlineRuntimeOptions());
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
    public async Task ConnectViaPacAsync_ServicePrincipal_ShouldThrow_WhenApplicationIdIsMissing()
    {
        var profile = new PacProfile { Kind = "ServicePrincipal", TenantId = "tenant-id" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ConnectViaPacAsync(profile, "https://test.crm.dynamics.com"));

        Assert.Contains("ApplicationId", ex.Message);
    }

    [Fact]
    public async Task ConnectViaPacAsync_ShouldThrow_WhenProfileIsNull()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => _service.ConnectViaPacAsync(null!, "https://test.crm.dynamics.com"));
    }

    [Fact]
    public async Task ConnectViaPacAsync_ShouldThrow_WhenEnvironmentUrlIsNull()
    {
        // Arrange
        var profile = new PacProfile { User = "test@test.com" };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _service.ConnectViaPacAsync(profile, null!));
    }

    [Fact(Skip = "Failing with 'Need a non-empty authority' error in current environment")]
    public async Task ConnectViaPacAsync_ShouldConnect_WhenRealEnvironmentUrlIsProvided()
    {
        // This test requires a valid PAC profile to be present on the machine
        var profiles = _service.GetPacProfiles();
        var profile = profiles.FirstOrDefault();

        if (profile == null) return;

        var environmentUrl = "https://test.crm.dynamics.com"; // Use a dummy URL for logic test

        // Since it's a dummy URL, it will fail to connect but we test that it attempts with correct parameters
        await Assert.ThrowsAnyAsync<Exception>(() => _service.ConnectViaPacAsync(profile, environmentUrl));
    }

    [Fact(Skip = "Opens a device code flow window in the browser, which is not supported in CI")]
    public async Task ConnectViaPacAsync_Universal_ShouldConnect_WhenEnvironmentUrlIsProvided()
    {
        // This test requires a valid UNIVERSAL PAC profile to be present on the machine
        var profiles = _service.GetPacProfiles();
        var profile = profiles.FirstOrDefault(p => p.IsUniversal);
        var environmentUrl = "https://spotlerautomate.crm4.dynamics.com";

        if (profile != null && environmentUrl != null)
        {
            var client = await _service.ConnectViaPacAsync(profile, environmentUrl);
            Assert.NotNull(client);
            if (client is ServiceClient sc)
            {
                Assert.True(sc.IsReady);
            }
        }
    }

    [Fact]
    public void BuildXrmContextConnectionString_UniversalProfile_ReturnsOAuthConnectionString()
    {
        var profiles = new List<PacProfile>
        {
            new() { Kind = "UNIVERSAL", Resource = "https://contoso.crm4.dynamics.com" }
        };

        var result = DataverseConnector.BuildXrmContextConnectionString("https://contoso.crm4.dynamics.com", profiles);

        Assert.Contains("AuthType=OAuth", result);
        Assert.Contains("Url=https://contoso.crm4.dynamics.com", result);
        Assert.Contains("AppId=51f81489-12ee-4a9e-aaae-a2591f45987d", result); // PAC CLI app — already consented
    }

    [Fact]
    public void BuildXrmContextConnectionString_WithUsernamePassword_IncludesCredentialsAndLoginPromptNever()
    {
        var profiles = new List<PacProfile>
        {
            new() { Kind = "UNIVERSAL", Resource = "https://contoso.crm4.dynamics.com" }
        };

        var result = DataverseConnector.BuildXrmContextConnectionString(
            "https://contoso.crm4.dynamics.com", profiles, username: "user@contoso.com", password: "secret");

        Assert.Contains("Username=user@contoso.com", result);
        Assert.Contains("Password=secret", result);
        Assert.Contains("LoginPrompt=Never", result);
    }

    [Fact]
    public void BuildXrmContextConnectionString_CustomClientId_UsesProvidedAppId()
    {
        var profiles = new List<PacProfile>
        {
            new() { Kind = "UNIVERSAL", Resource = "https://contoso.crm4.dynamics.com" }
        };

        var result = DataverseConnector.BuildXrmContextConnectionString(
            "https://contoso.crm4.dynamics.com", profiles, clientId: "my-custom-app-id");

        Assert.Contains("AppId=my-custom-app-id", result);
        Assert.DoesNotContain("AppId=51f81489", result);
    }

    [Fact]
    public void BuildXrmContextConnectionString_TrailingSlashNormalized_UrlHasNoTrailingSlash()
    {
        var profiles = new List<PacProfile>
        {
            new() { Kind = "UNIVERSAL", Resource = "https://contoso.crm4.dynamics.com" }
        };

        var result = DataverseConnector.BuildXrmContextConnectionString("https://contoso.crm4.dynamics.com/", profiles);

        Assert.Contains("Url=https://contoso.crm4.dynamics.com;", result);
        Assert.DoesNotContain("Url=https://contoso.crm4.dynamics.com/", result);
    }

    [Fact]
    public void BuildXrmContextConnectionString_NoProfileFound_ThrowsFlowlineException()
    {
        var ex = Assert.Throws<FlowlineException>(
            () => DataverseConnector.BuildXrmContextConnectionString(
                "https://no-such-env.crm.dynamics.com",
                Enumerable.Empty<PacProfile>()));

        Assert.Equal(ExitCode.NotAuthenticated, ex.ExitCode);
        Assert.Contains("No PAC profile found", ex.Message);
    }

    [Fact]
    public void BuildXrmContextConnectionString_ServicePrincipalProfile_ThrowsFlowlineException()
    {
        // PAC CLI does not store raw client secrets; XrmContext ClientSecret auth is not possible from a PAC profile alone.
        var profiles = new List<PacProfile>
        {
            new() { Kind = "ServicePrincipal", Resource = "https://contoso.crm4.dynamics.com", ApplicationId = "some-app-id" }
        };

        var ex = Assert.Throws<FlowlineException>(
            () => DataverseConnector.BuildXrmContextConnectionString("https://contoso.crm4.dynamics.com", profiles));

        Assert.Equal(ExitCode.NotAuthenticated, ex.ExitCode);
        Assert.Contains("service principal", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildXrmContextConnectionString_WithUsernameAndPassword_NoProfileRequired()
    {
        // ROPC path does not require a PAC profile
        var result = DataverseConnector.BuildXrmContextConnectionString(
            "https://contoso.crm4.dynamics.com",
            Enumerable.Empty<PacProfile>(),
            username: "user@contoso.com",
            password: "pass");

        Assert.Contains("Username=user@contoso.com", result);
        Assert.Contains("LoginPrompt=Never", result);
    }

    [Fact]
    public void BuildXrmContextConnectionString_UsernameSemicolon_ThrowsFlowlineException()
    {
        var ex = Assert.Throws<FlowlineException>(
            () => DataverseConnector.BuildXrmContextConnectionString(
                "https://contoso.crm4.dynamics.com",
                Enumerable.Empty<PacProfile>(),
                username: "user;evil=inject",
                password: "pass"));

        Assert.Equal(ExitCode.NotAuthenticated, ex.ExitCode);
    }

    [Fact]
    public void BuildXrmContextConnectionString_PasswordSemicolon_ThrowsFlowlineException()
    {
        var ex = Assert.Throws<FlowlineException>(
            () => DataverseConnector.BuildXrmContextConnectionString(
                "https://contoso.crm4.dynamics.com",
                Enumerable.Empty<PacProfile>(),
                username: "user@contoso.com",
                password: "pa;LoginPrompt=Auto"));

        Assert.Equal(ExitCode.NotAuthenticated, ex.ExitCode);
    }
}
