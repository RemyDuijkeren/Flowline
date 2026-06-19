using FluentAssertions;
using Flowline.Services;

namespace Flowline.Tests;

public class XrmContextRunnerTests
{
    const string EnvironmentUrl = "https://org.crm.dynamics.com";
    const string SolutionName = "MySolution";
    const string Namespace = "MySolution.Models";
    const string ConnectionStringValue = "AuthType=OAuth;Url=https://org.crm.dynamics.com";
    const string OutputPath = @"C:\temp\output";
    const string ClientId = "my-app-id";
    const string ClientSecret = "my-secret";
    static XrmContextAuth ConnectionStringAuth => new XrmContextAuth.ConnectionString(ConnectionStringValue);
    static XrmContextAuth ClientSecretAuth => new XrmContextAuth.ClientSecret(ClientId, ClientSecret);
    static XrmContextAuth BrowserOAuthAuth => new XrmContextAuth.BrowserOAuth();

    // ── ConnectionString method ──────────────────────────────────────────────

    [Fact]
    public void BuildArgs_ConnectionString_ContainsUrl()
    {
        var args = XrmContextRunner.BuildArgs(EnvironmentUrl, ConnectionStringAuth, SolutionName, null, Namespace, OutputPath);

        args.Should().Contain($"/url:{EnvironmentUrl}");
    }

    [Fact]
    public void BuildArgs_ConnectionString_ContainsUrl_TrailingSlashNormalized()
    {
        var args = XrmContextRunner.BuildArgs(EnvironmentUrl + "/", ConnectionStringAuth, SolutionName, null, Namespace, OutputPath);

        args.Should().Contain($"/url:{EnvironmentUrl}");
    }

    [Fact]
    public void BuildArgs_ConnectionString_ContainsMethodConnectionString()
    {
        var args = XrmContextRunner.BuildArgs(EnvironmentUrl, ConnectionStringAuth, SolutionName, null, Namespace, OutputPath);

        args.Should().Contain("/method:ConnectionString");
    }

    [Fact]
    public void BuildArgs_ConnectionString_ContainsConnectionString()
    {
        var args = XrmContextRunner.BuildArgs(EnvironmentUrl, ConnectionStringAuth, SolutionName, null, Namespace, OutputPath);

        args.Should().Contain($"/connectionString:{ConnectionStringValue}");
    }

    // ── ClientSecret method ──────────────────────────────────────────────────

    [Fact]
    public void BuildArgs_ClientSecret_ContainsOrgSvcUrl()
    {
        var args = XrmContextRunner.BuildArgs(EnvironmentUrl, ClientSecretAuth, SolutionName, null, Namespace, OutputPath);

        args.Should().Contain($"/url:{EnvironmentUrl}/XRMServices/2011/Organization.svc");
    }

    [Fact]
    public void BuildArgs_ClientSecret_ContainsMethodClientSecret()
    {
        var args = XrmContextRunner.BuildArgs(EnvironmentUrl, ClientSecretAuth, SolutionName, null, Namespace, OutputPath);

        args.Should().Contain("/method:ClientSecret");
    }

    [Fact]
    public void BuildArgs_ClientSecret_ContainsMfaAppId()
    {
        var args = XrmContextRunner.BuildArgs(EnvironmentUrl, ClientSecretAuth, SolutionName, null, Namespace, OutputPath);

        args.Should().Contain($"/mfaAppId:{ClientId}");
    }

    [Fact]
    public void BuildArgs_ClientSecret_ContainsMfaClientSecret()
    {
        var args = XrmContextRunner.BuildArgs(EnvironmentUrl, ClientSecretAuth, SolutionName, null, Namespace, OutputPath);

        args.Should().Contain($"/mfaClientSecret:{ClientSecret}");
    }

    [Fact]
    public void BuildArgs_ClientSecret_OrgSvcUrl_TrailingSlashNormalized()
    {
        var args = XrmContextRunner.BuildArgs(EnvironmentUrl + "/", ClientSecretAuth, SolutionName, null, Namespace, OutputPath);

        args.Should().Contain($"/url:{EnvironmentUrl}/XRMServices/2011/Organization.svc");
    }

    // ── BrowserOAuth method ──────────────────────────────────────────────────

    [Fact]
    public void BuildArgs_BrowserOAuth_ContainsOrgSvcUrl()
    {
        var args = XrmContextRunner.BuildArgs(EnvironmentUrl, BrowserOAuthAuth, SolutionName, null, Namespace, OutputPath);

        args.Should().Contain($"/url:{EnvironmentUrl}/XRMServices/2011/Organization.svc");
    }

    [Fact]
    public void BuildArgs_BrowserOAuth_ContainsMethodOAuth()
    {
        var args = XrmContextRunner.BuildArgs(EnvironmentUrl, BrowserOAuthAuth, SolutionName, null, Namespace, OutputPath);

        args.Should().Contain("/method:OAuth");
    }

    [Fact]
    public void BuildArgs_BrowserOAuth_ContainsPacAppId()
    {
        var args = XrmContextRunner.BuildArgs(EnvironmentUrl, BrowserOAuthAuth, SolutionName, null, Namespace, OutputPath);

        args.Should().Contain("/mfaAppId:51f81489-12ee-4a9e-aaae-a2591f45987d");
    }

    [Fact]
    public void BuildArgs_BrowserOAuth_ContainsLocalhostReturnUrl()
    {
        var args = XrmContextRunner.BuildArgs(EnvironmentUrl, BrowserOAuthAuth, SolutionName, null, Namespace, OutputPath);

        args.Should().Contain("/mfaReturnUrl:http://localhost");
    }

    [Fact]
    public void BuildArgs_BrowserOAuth_DoesNotContainTokenCacheStorePath()
    {
        // XrmContext 3.0.1 method:OAuth does not accept tokenCacheStorePath
        var args = XrmContextRunner.BuildArgs(EnvironmentUrl, BrowserOAuthAuth, SolutionName, null, Namespace, OutputPath);

        args.Should().NotContain(a => a.StartsWith("/tokenCacheStorePath:"));
    }

    [Fact]
    public void BuildArgs_BrowserOAuth_OrgSvcUrl_TrailingSlashNormalized()
    {
        var args = XrmContextRunner.BuildArgs(EnvironmentUrl + "/", BrowserOAuthAuth, SolutionName, null, Namespace, OutputPath);

        args.Should().Contain($"/url:{EnvironmentUrl}/XRMServices/2011/Organization.svc");
    }

    [Fact]
    public void BuildArgs_BrowserOAuth_ExplicitAppId_UsesProvidedAppId()
    {
        var auth = new XrmContextAuth.BrowserOAuth(AppId: ClientId);

        var args = XrmContextRunner.BuildArgs(EnvironmentUrl, auth, SolutionName, null, Namespace, OutputPath);

        args.Should().Contain($"/mfaAppId:{ClientId}");
        args.Should().NotContain("/mfaAppId:51f81489-12ee-4a9e-aaae-a2591f45987d");
    }

    // ── Common args (both methods) ───────────────────────────────────────────

    [Theory]
    [MemberData(nameof(BothAuthModes))]
    public void BuildArgs_ContainsSolutions(XrmContextAuth auth)
    {
        var args = XrmContextRunner.BuildArgs(EnvironmentUrl, auth, SolutionName, null, Namespace, OutputPath);

        args.Should().Contain($"/solutions:{SolutionName}");
    }

    [Theory]
    [MemberData(nameof(BothAuthModes))]
    public void BuildArgs_ContainsNamespace(XrmContextAuth auth)
    {
        var args = XrmContextRunner.BuildArgs(EnvironmentUrl, auth, SolutionName, null, Namespace, OutputPath);

        args.Should().Contain($"/namespace:{Namespace}");
    }

    [Theory]
    [MemberData(nameof(BothAuthModes))]
    public void BuildArgs_ContainsOut(XrmContextAuth auth)
    {
        var args = XrmContextRunner.BuildArgs(EnvironmentUrl, auth, SolutionName, null, Namespace, OutputPath);

        args.Should().Contain($"/out:{OutputPath}");
    }

    // ── /oneFile ─────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(BothAuthModes))]
    public void BuildArgs_AlwaysContainsOneFileFalse(XrmContextAuth auth)
    {
        var args = XrmContextRunner.BuildArgs(EnvironmentUrl, auth, SolutionName, null, Namespace, OutputPath);

        args.Should().Contain("/oneFile:false");
    }

    // ── /entities conditional ────────────────────────────────────────────────

    [Fact]
    public void BuildArgs_ContainsEntities_WhenExtraTablesHasEntries()
    {
        var extraTables = new[] { "account", "contact" };

        var args = XrmContextRunner.BuildArgs(EnvironmentUrl, ConnectionStringAuth, SolutionName, extraTables, Namespace, OutputPath);

        args.Should().Contain("/entities:account,contact");
    }

    [Fact]
    public void BuildArgs_OmitsEntities_WhenExtraTablesIsNull()
    {
        var args = XrmContextRunner.BuildArgs(EnvironmentUrl, ConnectionStringAuth, SolutionName, null, Namespace, OutputPath);

        args.Should().NotContain(a => a.StartsWith("/entities:"));
    }

    [Fact]
    public void BuildArgs_OmitsEntities_WhenExtraTablesIsEmpty()
    {
        var args = XrmContextRunner.BuildArgs(EnvironmentUrl, ConnectionStringAuth, SolutionName, [], Namespace, OutputPath);

        args.Should().NotContain(a => a.StartsWith("/entities:"));
    }

    public static TheoryData<XrmContextAuth> BothAuthModes =>
        new() { ConnectionStringAuth, ClientSecretAuth, BrowserOAuthAuth };
}
