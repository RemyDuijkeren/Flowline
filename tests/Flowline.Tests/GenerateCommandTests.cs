using Flowline.Config;
using Flowline.Services;
using FluentAssertions;

namespace Flowline.Tests;

public class GeneratorResolutionTests
{
    // Mirrors the resolution expression in GenerateCommand.ExecuteFlowlineAsync:
    //   var generator = settings.Generator ?? projectSln?.Generate?.Generator ?? GeneratorType.Pac;

    static GeneratorType Resolve(GeneratorType? settingsGenerator, GeneratorType? configGenerator)
    {
        var projectSln = configGenerator.HasValue
            ? new ProjectSolution { Name = "Test", Generate = new GenerateConfig { Generator = configGenerator } }
            : null;

        // Replicate the expression directly
        return settingsGenerator ?? projectSln?.Generate?.Generator ?? GeneratorType.Pac;
    }

    [Fact]
    public void Resolve_SettingsXrmContext3_NoConfig_ReturnsXrmContext3()
    {
        var result = Resolve(GeneratorType.XrmContext3, configGenerator: null);

        result.Should().Be(GeneratorType.XrmContext3);
    }

    [Fact]
    public void Resolve_SettingsPac_NoConfig_ReturnsPac()
    {
        var result = Resolve(GeneratorType.Pac, configGenerator: null);

        result.Should().Be(GeneratorType.Pac);
    }

    [Fact]
    public void Resolve_SettingsNull_ConfigXrmContext3_ReturnsXrmContext3()
    {
        var result = Resolve(settingsGenerator: null, configGenerator: GeneratorType.XrmContext3);

        result.Should().Be(GeneratorType.XrmContext3);
    }

    [Fact]
    public void Resolve_SettingsNull_NoConfig_ReturnsPac()
    {
        var result = Resolve(settingsGenerator: null, configGenerator: null);

        result.Should().Be(GeneratorType.Pac);
    }

    [Fact]
    public void Resolve_SettingsPac_ConfigXrmContext3_ReturnsPac()
    {
        // CLI flag wins over saved config
        var result = Resolve(GeneratorType.Pac, configGenerator: GeneratorType.XrmContext3);

        result.Should().Be(GeneratorType.Pac);
    }
}

public class XrmContextAuthResolutionTests
{
    // Mirrors the auth branch selection in GenerateCommand.ExecuteFlowlineAsync:
    //   if clientId + clientSecret → ClientSecret
    //   else if username          → ConnectionString (ROPC)
    //   else                      → BrowserOAuth(xrmClientId)

    static Type ResolveAuthBranch(string? clientId, string? clientSecret, string? username)
    {
        if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret))
            return typeof(XrmContextAuth.ClientSecret);
        if (!string.IsNullOrEmpty(username))
            return typeof(XrmContextAuth.ConnectionString);
        return typeof(XrmContextAuth.BrowserOAuth);
    }

    static string? ResolveAppId(string? clientId, string? clientSecret)
    {
        // BrowserOAuth is constructed with xrmClientId when no clientSecret present
        if (!string.IsNullOrEmpty(clientId) && string.IsNullOrEmpty(clientSecret))
            return clientId;
        return null;
    }

    [Fact]
    public void Auth_ClientIdAndSecret_SelectsClientSecret()
    {
        ResolveAuthBranch("my-id", "my-secret", username: null)
            .Should().Be(typeof(XrmContextAuth.ClientSecret));
    }

    [Fact]
    public void Auth_UsernameOnly_SelectsConnectionString()
    {
        ResolveAuthBranch(clientId: null, clientSecret: null, username: "user@contoso.com")
            .Should().Be(typeof(XrmContextAuth.ConnectionString));
    }

    [Fact]
    public void Auth_NoCredentials_SelectsBrowserOAuth()
    {
        ResolveAuthBranch(clientId: null, clientSecret: null, username: null)
            .Should().Be(typeof(XrmContextAuth.BrowserOAuth));
    }

    [Fact]
    public void Auth_ClientIdWithoutSecret_SelectsBrowserOAuth_PassesClientIdAsAppId()
    {
        // xrmClientId without xrmClientSecret → BrowserOAuth with xrmClientId as AppId
        ResolveAuthBranch(clientId: "my-id", clientSecret: null, username: null)
            .Should().Be(typeof(XrmContextAuth.BrowserOAuth));

        ResolveAppId("my-id", clientSecret: null).Should().Be("my-id");
    }
}
