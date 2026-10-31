using Flowline.Core;
using Flowline.Core.Config;
using Flowline.Core.Environments;
using FluentAssertions;

namespace Flowline.Core.Tests;

public class EnvironmentTargetTests
{
    // ── What counts as a target ──────────────────────────────────────────────

    [Theory]
    [InlineData("dev")]
    [InlineData("PROD")]
    [InlineData("Uat")]
    [InlineData("https://contoso.crm4.dynamics.com")]
    [InlineData("http://localhost:5555")]
    public void IsTarget_RoleOrHttpUrl_ReturnsTrue(string value) =>
        EnvironmentTarget.IsTarget(value).Should().BeTrue();

    // A bare host is refused the same way --env refuses it: pass the full URL.
    [Theory]
    [InlineData("contoso.crm4.dynamics.com")]
    [InlineData("staging")]
    [InlineData("ftp://contoso.crm4.dynamics.com")]
    [InlineData("")]
    [InlineData(@"C:\temp\contoso")]
    public void IsTarget_NeitherRoleNorHttpUrl_ReturnsFalse(string value) =>
        EnvironmentTarget.IsTarget(value).Should().BeFalse();

    [Fact]
    public void IsUrl_RoleKeyword_ReturnsFalse() =>
        EnvironmentTarget.IsUrl("prod").Should().BeFalse();

    // ── Messages ─────────────────────────────────────────────────────────────

    [Fact]
    public void NotATarget_NamesTheValueListsRolesDevFirstAndEndsWithTheFix()
    {
        var message = EnvironmentTarget.NotATarget("staging", "Run: flowline drift <dev|test|uat|prod|url>");

        message.Should().StartWith("'staging' isn't a role (dev, test, uat, prod) or a URL.");
        message.Should().EndWith("Run: flowline drift <dev|test|uat|prod|url>");
    }

    [Fact]
    public void TargetPlaceholder_ListsRolesDevFirstThenUrl() =>
        EnvironmentTarget.TargetPlaceholder.Should().Be("<dev|test|uat|prod|url>");

    [Fact]
    public void StandaloneRole_NamesTheRoleAndSaysToPassTheUrl()
    {
        var message = EnvironmentTarget.StandaloneRole("prod");

        message.Should().Contain("'prod'");
        message.Should().Contain("Pass the environment URL instead");
    }

    // ── A role's saved URL ───────────────────────────────────────────────────

    [Fact]
    public void RequireRoleUrl_Saved_ReturnsIt()
    {
        var config = new ProjectConfig { UatUrl = "https://contoso-uat.crm4.dynamics.com/" };

        EnvironmentTarget.RequireRoleUrl(config, EnvironmentRole.Uat)
            .Should().Be("https://contoso-uat.crm4.dynamics.com/");
    }

    [Fact]
    public void RequireRoleUrl_NotSaved_ThrowsConfigInvalidNamingTheKey()
    {
        var act = () => EnvironmentTarget.RequireRoleUrl(new ProjectConfig(), EnvironmentRole.Uat);

        act.Should().Throw<FlowlineException>()
           .Where(e => e.ExitCode == ExitCode.ConfigInvalid)
           .Which.Message.Should().Contain("UatUrl").And.Contain(".flowline");
    }

    [Fact]
    public void RequireRoleUrl_NotSaved_AppendsTheCallersAlternative()
    {
        var act = () => EnvironmentTarget.RequireRoleUrl(new ProjectConfig(), EnvironmentRole.Test, "pass --env <url>");

        act.Should().Throw<FlowlineException>().Which.Message.Should().Contain("pass --env <url>");
    }

    // A non-URL saved under a role used to reach the PAC profile lookup as a "URL" and come back as an
    // auth error. It now fails up front, naming the key and the value so the fix is obvious.
    [Fact]
    public void RequireRoleUrl_SavedValueIsNotAUrl_ThrowsValidationFailedNamingKeyAndValue()
    {
        var config = new ProjectConfig { UatUrl = "not-a-url" };

        var act = () => EnvironmentTarget.RequireRoleUrl(config, EnvironmentRole.Uat);

        act.Should().Throw<FlowlineException>()
           .Where(e => e.ExitCode == ExitCode.ValidationFailed)
           .Which.Message.Should().Contain("UatUrl").And.Contain("not-a-url").And.Contain(".flowline");
    }
}
