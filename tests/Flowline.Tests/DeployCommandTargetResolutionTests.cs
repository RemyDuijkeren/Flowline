using FluentAssertions;
using Flowline.Commands;
using Flowline.Config;
using Flowline.Core;

namespace Flowline.Tests;

// Regression coverage for ResolveTargetUrl falling through an unrecognized role name (e.g. a typo'd
// target) as a literal string with no URL validation — it used to reach MSAL as a token scope and
// crash with a raw AADSTS70011 stack trace instead of a clean FlowlineException.
public class DeployCommandTargetResolutionTests
{
    [Theory]
    [InlineData("prod", "https://contoso.crm4.dynamics.com")]
    [InlineData("PROD", "https://contoso.crm4.dynamics.com")]
    [InlineData("dev", "https://contoso-dev.crm4.dynamics.com")]
    public void ResolveTargetUrl_KnownRole_ReturnsConfiguredUrl(string target, string expectedUrl)
    {
        var config = new ProjectConfig
        {
            ProdUrl = "https://contoso.crm4.dynamics.com",
            DevUrl = "https://contoso-dev.crm4.dynamics.com",
        };

        DeployCommand.ResolveTargetUrl(target, config).Should().Be(expectedUrl);
    }

    [Fact]
    public void ResolveTargetUrl_ExplicitUrl_ReturnsItVerbatim()
    {
        var config = new ProjectConfig();

        DeployCommand.ResolveTargetUrl("https://contoso-uat.crm4.dynamics.com", config)
            .Should().Be("https://contoso-uat.crm4.dynamics.com");
    }

    [Fact]
    public void ResolveTargetUrl_UnrecognizedNonUrlTarget_ThrowsValidationFailed()
    {
        var config = new ProjectConfig();

        var act = () => DeployCommand.ResolveTargetUrl("bogus-target", config);

        act.Should().Throw<FlowlineException>()
           .Where(ex => ex.ExitCode == ExitCode.ValidationFailed)
           .Where(ex => ex.Message.Contains("bogus-target"));
    }

    [Fact]
    public void ResolveTargetUrl_UnconfiguredKnownRole_ThrowsConfigInvalid()
    {
        var config = new ProjectConfig();

        var act = () => DeployCommand.ResolveTargetUrl("uat", config);

        act.Should().Throw<FlowlineException>()
           .Where(ex => ex.ExitCode == ExitCode.ConfigInvalid);
    }

    // R15: a role keyword with no resolvable config, in standalone, must point at the missing URL
    // rather than at a .flowline that standalone never expects to exist.
    [Fact]
    public void ResolveTargetUrl_UnconfiguredKnownRole_Standalone_ThrowsRewordedMessage()
    {
        var config = new ProjectConfig();

        var act = () => DeployCommand.ResolveTargetUrl("uat", config, standalone: true);

        act.Should().Throw<FlowlineException>()
           .Where(ex => ex.ExitCode == ExitCode.ConfigInvalid)
           .Where(ex => !ex.Message.Contains(".flowline"));
    }

    // R1/F1: a full URL target in standalone resolves without touching config at all — same
    // fall-through as project mode, just proven under the standalone wording flag too.
    [Fact]
    public void ResolveTargetUrl_ExplicitUrl_Standalone_ReturnsItVerbatim()
    {
        var config = new ProjectConfig();

        DeployCommand.ResolveTargetUrl("https://contoso-uat.crm4.dynamics.com", config, standalone: true)
            .Should().Be("https://contoso-uat.crm4.dynamics.com");
    }
}
