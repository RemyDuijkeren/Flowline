using Flowline;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Microsoft.Identity.Client;
using Microsoft.PowerPlatform.Dataverse.Client;
using Spectre.Console.Testing;
using Xunit;

namespace Flowline.Core.Tests;

file sealed class FakeAccount(string username, string tenantId, string identifier = "id") : IAccount
{
    public string Username { get; } = username;
    public string Environment => "login.microsoftonline.com";
    public AccountId HomeAccountId { get; } = new(identifier, identifier, tenantId);
}

public class DataverseConnectorTests
{
    private readonly DataverseConnector _service;

    public DataverseConnectorTests()
    {
        _service = new DataverseConnector(new TestConsole(), new HttpClient());
    }

    [Fact(Skip = "Requires PAC CLI auth profile file on the machine — not available in CI")]
    public void GetPacProfiles_ShouldReturnProfiles_WhenFileExists()
    {
        var profiles = _service.GetPacProfiles();
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

        var ex = await Assert.ThrowsAsync<FlowlineException>(
            () => _service.ConnectViaPacAsync(profile, "https://test.crm.dynamics.com"));

        Assert.Equal(ExitCode.NotAuthenticated, ex.ExitCode);
        Assert.Contains("ApplicationId", ex.Message);
    }

    [Fact]
    public async Task GetEnvironmentInfoAsync_ServicePrincipal_ShouldThrow_WhenApplicationIdIsMissing()
    {
        var profile = new PacProfile { Kind = "ServicePrincipal", TenantId = "tenant-id" };

        var ex = await Assert.ThrowsAsync<FlowlineException>(
            () => _service.GetEnvironmentInfoAsync(profile, "https://test.crm.dynamics.com"));

        Assert.Equal(ExitCode.NotAuthenticated, ex.ExitCode);
        Assert.Contains("ApplicationId", ex.Message);
    }

    [Fact]
    public async Task GetEnvironmentInfoAsync_ShouldThrow_WhenProfileIsNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _service.GetEnvironmentInfoAsync(null!, "https://test.crm.dynamics.com"));
    }

    [Fact]
    public async Task GetEnvironmentInfoAsync_ShouldThrow_WhenEnvironmentUrlIsNull()
    {
        var profile = new PacProfile { User = "test@test.com" };

        await Assert.ThrowsAsync<ArgumentException>(() => _service.GetEnvironmentInfoAsync(profile, null!));
    }

    static string RetrieveCurrentOrganizationJson(string organizationType) => $$"""
        {
          "Detail": {
            "OrganizationId": "22222222-2222-2222-2222-222222222222",
            "EnvironmentId": "11111111-1111-1111-1111-111111111111",
            "FriendlyName": "Contoso",
            "UniqueName": "unqcontoso",
            "UrlName": "contoso",
            "State": "Enabled",
            "OrganizationType": "{{organizationType}}",
            "OrganizationVersion": "9.2.23092.00206"
          }
        }
        """;

    [Fact]
    public void MapRetrieveCurrentOrganizationResponse_SecondaryType_ReturnsProductionType()
    {
        var result = DataverseConnector.MapRetrieveCurrentOrganizationResponse(
            RetrieveCurrentOrganizationJson("Secondary"), "https://contoso.crm4.dynamics.com");

        Assert.NotNull(result);
        Assert.Equal("Production", result!.Type);
        Assert.Equal("Contoso", result.DisplayName);
    }

    [Fact]
    public void MapRetrieveCurrentOrganizationResponse_CustomerTestType_ReturnsSandboxType()
    {
        var result = DataverseConnector.MapRetrieveCurrentOrganizationResponse(
            RetrieveCurrentOrganizationJson("CustomerTest"), "https://contoso.crm4.dynamics.com");

        Assert.NotNull(result);
        Assert.Equal("Sandbox", result!.Type);
    }

    [Fact]
    public void MapRetrieveCurrentOrganizationResponse_UnrecognizedType_PassesThroughRawValue()
    {
        var result = DataverseConnector.MapRetrieveCurrentOrganizationResponse(
            RetrieveCurrentOrganizationJson("Trial"), "https://contoso.crm4.dynamics.com");

        Assert.NotNull(result);
        Assert.Equal("Trial", result!.Type);
    }

    [Fact]
    public void MapRetrieveCurrentOrganizationResponse_UrlNormalizedToRequestedEnvironmentUrl()
    {
        var result = DataverseConnector.MapRetrieveCurrentOrganizationResponse(
            RetrieveCurrentOrganizationJson("Secondary"), "https://contoso.crm4.dynamics.com/");

        Assert.NotNull(result);
        Assert.Equal("https://contoso.crm4.dynamics.com", result!.EnvironmentUrl);
    }

    [Fact]
    public void MapRetrieveCurrentOrganizationResponse_MissingDetailProperty_ReturnsNull()
    {
        var result = DataverseConnector.MapRetrieveCurrentOrganizationResponse("""{ "other": {} }""", "https://contoso.crm4.dynamics.com");

        Assert.Null(result);
    }

    [Fact]
    public void MapRetrieveCurrentOrganizationResponse_DetailIsNotObject_ReturnsNull()
    {
        var result = DataverseConnector.MapRetrieveCurrentOrganizationResponse("""{ "Detail": "not-an-object" }""", "https://contoso.crm4.dynamics.com");

        Assert.Null(result);
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

    [Fact(Skip = "Requires a valid PAC CLI auth profile — not available in CI")]
    public async Task GetEnvironmentInfoAsync_NeverSetsSharedHttpClientDefaultAuthorizationHeader()
    {
        // Regression guard: the acquired bearer token must be attached per-request
        // (HttpRequestMessage.Headers.Authorization), never on HttpClient.DefaultRequestHeaders —
        // that HttpClient may be a shared singleton also used for unrelated calls (e.g. NuGet
        // downloads), and DefaultRequestHeaders would leak the admin-scoped token onto those too.
        var httpClient = new HttpClient();
        var service = new DataverseConnector(new TestConsole(), httpClient);
        var profile = service.GetPacProfiles().FirstOrDefault();
        if (profile == null) return;

        await service.GetEnvironmentInfoAsync(profile, "https://contoso.crm4.dynamics.com");

        Assert.Null(httpClient.DefaultRequestHeaders.Authorization);
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
        Assert.Contains("AppId=9cee029c-6210-4654-90bb-17e6e9d36617", result); // PAC CLI app — already consented
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
        Assert.DoesNotContain("AppId=9cee029c", result);
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
    public void FindBestProfile_SingleCandidateMatchesUrl_ReturnsProfileFound()
    {
        var profile = new PacProfile { Kind = "DATAVERSE", Resource = "https://contoso.crm4.dynamics.com" };
        var profiles = new PacAuthProfiles { Profiles = [profile] };

        var result = DataverseConnector.FindBestProfile("https://contoso.crm4.dynamics.com", profiles);

        var found = Assert.IsType<ProfileFound>(result);
        Assert.Same(profile, found.Profile);
    }

    [Fact]
    public void FindBestProfile_MultipleCandidates_OneMatchesCurrent_ReturnsProfileFound()
    {
        var profileA = new PacProfile { Kind = "DATAVERSE", Resource = "https://contoso.crm4.dynamics.com", User = "a@contoso.com" };
        var profileB = new PacProfile { Kind = "DATAVERSE", Resource = "https://contoso.crm4.dynamics.com", User = "b@contoso.com" };
        var profiles = new PacAuthProfiles
        {
            Profiles = [profileA, profileB],
            Current = new Dictionary<string, PacProfile> { ["DATAVERSE"] = profileB }
        };

        var result = DataverseConnector.FindBestProfile("https://contoso.crm4.dynamics.com", profiles);

        var found = Assert.IsType<ProfileFound>(result);
        Assert.Same(profileB, found.Profile);
    }

    [Fact]
    public void FindBestProfile_MultipleCandidates_NoneMatchesCurrent_ReturnsProfileAmbiguous()
    {
        var profileA = new PacProfile { Kind = "DATAVERSE", Resource = "https://contoso.crm4.dynamics.com", User = "a@contoso.com" };
        var profileB = new PacProfile { Kind = "DATAVERSE", Resource = "https://contoso.crm4.dynamics.com", User = "b@contoso.com" };
        var other    = new PacProfile { Kind = "DATAVERSE", Resource = "https://contoso.crm4.dynamics.com", User = "c@contoso.com" };
        var profiles = new PacAuthProfiles
        {
            Profiles = [profileA, profileB],
            Current = new Dictionary<string, PacProfile> { ["DATAVERSE"] = other }
        };

        var result = DataverseConnector.FindBestProfile("https://contoso.crm4.dynamics.com", profiles);

        var ambiguous = Assert.IsType<ProfileAmbiguous>(result);
        Assert.Equal(2, ambiguous.Candidates.Count);
    }

    [Fact]
    public void FindBestProfile_MultipleCandidatesMultipleMatchCurrent_ReturnsFirstActiveMatch()
    {
        // Two different Kinds both active and both matching the URL — first candidate whose Kind hits Current wins
        var profileA = new PacProfile { Kind = "DATAVERSE",    Resource = "https://contoso.crm4.dynamics.com", User = "a@contoso.com" };
        var profileB = new PacProfile { Kind = "ServicePrincipal", Resource = "https://contoso.crm4.dynamics.com", ApplicationId = "app-id" };
        var profiles = new PacAuthProfiles
        {
            Profiles = [profileA, profileB],
            Current = new Dictionary<string, PacProfile>
            {
                ["DATAVERSE"]        = profileA,
                ["ServicePrincipal"] = profileB
            }
        };

        var result = DataverseConnector.FindBestProfile("https://contoso.crm4.dynamics.com", profiles);

        var found = Assert.IsType<ProfileFound>(result);
        Assert.Same(profileA, found.Profile); // profileA comes first in Profiles list
    }

    [Fact]
    public void FindBestProfile_NoUrlMatch_UniversalInCurrent_ReturnsProfileFound()
    {
        var universal = new PacProfile { Kind = "UNIVERSAL" };
        var profiles = new PacAuthProfiles
        {
            Profiles = [new PacProfile { Kind = "DATAVERSE", Resource = "https://other.crm4.dynamics.com" }],
            Current = new Dictionary<string, PacProfile> { ["UNIVERSAL"] = universal }
        };

        var result = DataverseConnector.FindBestProfile("https://contoso.crm4.dynamics.com", profiles);

        var found = Assert.IsType<ProfileFound>(result);
        Assert.Same(universal, found.Profile);
    }

    [Fact]
    public void FindBestProfile_NoUrlMatch_NoUniversal_ReturnsProfileNotFound()
    {
        var profiles = new PacAuthProfiles
        {
            Profiles = [new PacProfile { Kind = "DATAVERSE", Resource = "https://other.crm4.dynamics.com" }],
            Current = new Dictionary<string, PacProfile>
            {
                ["DATAVERSE"] = new PacProfile { Kind = "DATAVERSE", Resource = "https://other.crm4.dynamics.com" }
            }
        };

        var result = DataverseConnector.FindBestProfile("https://contoso.crm4.dynamics.com", profiles);

        var notFound = Assert.IsType<ProfileNotFound>(result);
        Assert.Equal("https://contoso.crm4.dynamics.com", notFound.EnvironmentUrl);
    }

    [Fact]
    public void FindBestProfile_EmptyProfiles_ReturnsProfileNotFound()
    {
        var profiles = new PacAuthProfiles { Profiles = [] };

        var result = DataverseConnector.FindBestProfile("https://contoso.crm4.dynamics.com", profiles);

        Assert.IsType<ProfileNotFound>(result);
    }

    [Fact]
    public void FindBestProfile_CurrentIsNull_SingleUrlMatch_ReturnsProfileFound()
    {
        var profile = new PacProfile { Kind = "DATAVERSE", Resource = "https://contoso.crm4.dynamics.com" };
        var profiles = new PacAuthProfiles { Profiles = [profile], Current = null };

        var result = DataverseConnector.FindBestProfile("https://contoso.crm4.dynamics.com", profiles);

        var found = Assert.IsType<ProfileFound>(result);
        Assert.Same(profile, found.Profile);
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

        Assert.Equal(ExitCode.ValidationFailed, ex.ExitCode);
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

        Assert.Equal(ExitCode.ValidationFailed, ex.ExitCode);
    }

    [Fact]
    public void LoadPacAuthProfiles_FileNotFound_ThrowsFlowlineExceptionWithActionableMessage()
    {
        var nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");

        var ex = Assert.Throws<FlowlineException>(() => _service.LoadPacAuthProfiles(nonExistentPath));

        Assert.Equal(ExitCode.NotAuthenticated, ex.ExitCode);
        Assert.Contains("pac auth create", ex.Message);
    }

    [Fact]
    public void LoadPacAuthProfiles_MalformedJson_ThrowsFlowlineExceptionWithParseMessage()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "{ this is not valid json }}}");

            var ex = Assert.Throws<FlowlineException>(() => _service.LoadPacAuthProfiles(tempFile));

            Assert.Equal(ExitCode.NotAuthenticated, ex.ExitCode);
            Assert.Contains("parse", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void LoadPacAuthProfiles_JsonDeserializesToNull_ThrowsFlowlineException()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "null");

            var ex = Assert.Throws<FlowlineException>(() => _service.LoadPacAuthProfiles(tempFile));

            Assert.Equal(ExitCode.NotAuthenticated, ex.ExitCode);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void LoadPacAuthProfiles_ValidJson_ReturnsProfiles()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var json = """{"Profiles":[{"Kind":"DATAVERSE","Resource":"https://contoso.crm4.dynamics.com"}],"Current":{}}""";
            File.WriteAllText(tempFile, json);

            var result = _service.LoadPacAuthProfiles(tempFile);

            Assert.NotNull(result);
            Assert.Single(result.Profiles!);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // SelectCachedAccount: regression coverage for a real bug found in the field. The same
    // username can be cached under multiple home tenants (e.g. a stale guest/B2B entry from an
    // unrelated org alongside the real member account for the profile's actual tenant).
    // AcquireTokenSilent picking the wrong one produced a confusing AADSTS90072 "account doesn't
    // exist in tenant" failure for a genuinely valid, already-re-authenticated account.

    [Fact]
    public void SelectCachedAccount_SingleMatchingUsername_ReturnsIt()
    {
        var account = new FakeAccount("user@contoso.com", "tenant-a");

        var result = DataverseConnector.SelectCachedAccount([account], "user@contoso.com", tenantId: null);

        Assert.Same(account, result);
    }

    [Fact]
    public void SelectCachedAccount_SameUsernameMultipleTenants_PrefersMatchingTenantId()
    {
        var wrongTenant = new FakeAccount("user@contoso.com", "tenant-wrong", "id-1");
        var rightTenant = new FakeAccount("user@contoso.com", "tenant-right", "id-2");

        var result = DataverseConnector.SelectCachedAccount([wrongTenant, rightTenant], "user@contoso.com", "tenant-right");

        Assert.Same(rightTenant, result);
    }

    [Fact]
    public void SelectCachedAccount_TenantIdMatchIsCaseInsensitive()
    {
        var account = new FakeAccount("user@contoso.com", "Tenant-ABC");

        var result = DataverseConnector.SelectCachedAccount([account], "user@contoso.com", "tenant-abc");

        Assert.Same(account, result);
    }

    [Fact]
    public void SelectCachedAccount_NoTenantIdRecorded_FallsBackToFirstUsernameMatch()
    {
        var first = new FakeAccount("user@contoso.com", "tenant-a", "id-1");
        var second = new FakeAccount("user@contoso.com", "tenant-b", "id-2");

        var result = DataverseConnector.SelectCachedAccount([first, second], "user@contoso.com", tenantId: null);

        Assert.Same(first, result);
    }

    [Fact]
    public void SelectCachedAccount_TenantIdSetButNoAccountMatchesIt_FallsBackToFirstUsernameMatch()
    {
        var first = new FakeAccount("user@contoso.com", "tenant-a", "id-1");
        var second = new FakeAccount("user@contoso.com", "tenant-b", "id-2");

        var result = DataverseConnector.SelectCachedAccount([first, second], "user@contoso.com", "tenant-c-not-cached");

        Assert.Same(first, result);
    }

    [Fact]
    public void SelectCachedAccount_NoUsernameGiven_ReturnsFirstAccount()
    {
        var first = new FakeAccount("a@contoso.com", "tenant-a");
        var second = new FakeAccount("b@contoso.com", "tenant-b");

        var result = DataverseConnector.SelectCachedAccount([first, second], username: null, tenantId: null);

        Assert.Same(first, result);
    }

    [Fact]
    public void SelectCachedAccount_UsernameNotCached_FallsBackToAnyAccount()
    {
        var other = new FakeAccount("someone-else@contoso.com", "tenant-a");

        var result = DataverseConnector.SelectCachedAccount([other], "user@contoso.com", tenantId: null);

        Assert.Same(other, result);
    }

    [Fact]
    public void SelectCachedAccount_NoAccountsCached_ReturnsNull()
    {
        var result = DataverseConnector.SelectCachedAccount([], "user@contoso.com", "tenant-a");

        Assert.Null(result);
    }
}
