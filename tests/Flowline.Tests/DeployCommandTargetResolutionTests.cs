using FluentAssertions;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Core.Config;
using Flowline.Core.Environments;

namespace Flowline.Tests;

// Regression coverage for ResolveTarget falling through an unrecognized role name (e.g. a typo'd
// target) as a literal string with no URL validation — it used to reach MSAL as a token scope and
// crash with a raw AADSTS70011 stack trace instead of a clean FlowlineException.
public class DeployCommandTargetResolutionTests
{
    [Theory]
    [InlineData("prod", "https://contoso.crm4.dynamics.com", EnvironmentRole.Prod)]
    [InlineData("PROD", "https://contoso.crm4.dynamics.com", EnvironmentRole.Prod)]
    [InlineData("dev", "https://contoso-dev.crm4.dynamics.com", EnvironmentRole.Dev)]
    [InlineData("UAT", "https://contoso-uat.crm4.dynamics.com", EnvironmentRole.Uat)]
    public void ResolveTarget_KnownRole_ReturnsConfiguredUrlAndTheRole(string target, string expectedUrl, EnvironmentRole expectedRole)
    {
        var config = new ProjectConfig
        {
            ProdUrl = "https://contoso.crm4.dynamics.com",
            UatUrl = "https://contoso-uat.crm4.dynamics.com",
            DevUrl = "https://contoso-dev.crm4.dynamics.com",
        };

        DeployCommand.ResolveTarget(target, config).Should().Be((expectedUrl, expectedRole));
    }

    // A URL carries no role, so the environment-type guard has nothing to check it against.
    [Fact]
    public void ResolveTarget_ExplicitUrl_ReturnsItVerbatimWithNoRole()
    {
        var config = new ProjectConfig();

        DeployCommand.ResolveTarget("https://contoso-uat.crm4.dynamics.com", config)
            .Should().Be(("https://contoso-uat.crm4.dynamics.com", (EnvironmentRole?)null));
    }

    [Fact]
    public void ResolveTarget_UnrecognizedNonUrlTarget_ThrowsValidationFailedWithTheLineToRun()
    {
        var config = new ProjectConfig();

        var act = () => DeployCommand.ResolveTarget("bogus-target", config);

        act.Should().Throw<FlowlineException>()
           .Where(ex => ex.ExitCode == ExitCode.ValidationFailed)
           .Which.Message.Should().Contain("'bogus-target'").And.Contain("flowline deploy <dev|test|uat|prod|url>");
    }

    [Fact]
    public void ResolveTarget_UnconfiguredKnownRole_ThrowsConfigInvalidNamingTheKey()
    {
        var config = new ProjectConfig();

        var act = () => DeployCommand.ResolveTarget("uat", config);

        act.Should().Throw<FlowlineException>()
           .Where(ex => ex.ExitCode == ExitCode.ConfigInvalid)
           .Which.Message.Should().Contain("UatUrl");
    }

    // A non-URL saved under a role names the key to fix, and never claims the role isn't a role.
    [Fact]
    public void ResolveTarget_RoleWhoseSavedValueIsNotAUrl_ThrowsValidationFailedNamingTheKey()
    {
        var config = new ProjectConfig { UatUrl = "not-a-url" };

        var act = () => DeployCommand.ResolveTarget("uat", config);

        act.Should().Throw<FlowlineException>()
           .Where(ex => ex.ExitCode == ExitCode.ValidationFailed)
           .Which.Message.Should().Contain("UatUrl").And.Contain(".flowline").And.NotContain("isn't a role");
    }

    // A role outside a project has nothing to resolve against: the shared refusal says to pass the URL.
    [Fact]
    public void ResolveTarget_Role_Standalone_ThrowsConfigInvalidWithTheSharedRefusal()
    {
        var config = new ProjectConfig();

        var act = () => DeployCommand.ResolveTarget("uat", config, standalone: true);

        act.Should().Throw<FlowlineException>()
           .Where(ex => ex.ExitCode == ExitCode.ConfigInvalid)
           .Which.Message.Should().Be(EnvironmentTarget.StandaloneRole("uat"));
    }

    // A full URL target in standalone resolves without touching config at all.
    [Fact]
    public void ResolveTarget_ExplicitUrl_Standalone_ReturnsItVerbatim()
    {
        var config = new ProjectConfig();

        DeployCommand.ResolveTarget("https://contoso-uat.crm4.dynamics.com", config, standalone: true)
            .Should().Be(("https://contoso-uat.crm4.dynamics.com", (EnvironmentRole?)null));
    }
}
