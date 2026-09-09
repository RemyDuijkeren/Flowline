using Flowline.Core.Services;
using FluentAssertions;

namespace Flowline.Core.Tests;

public class EnvironmentRoleInferenceTests
{
    [Theory]
    [InlineData("https://contoso-dev.crm4.dynamics.com", InferredRole.Dev)]
    [InlineData("https://contoso-develop.crm4.dynamics.com", InferredRole.Dev)]
    [InlineData("https://contoso-development.crm4.dynamics.com", InferredRole.Dev)]
    [InlineData("https://contoso-test.crm4.dynamics.com", InferredRole.Test)]
    [InlineData("https://contoso-tst.crm4.dynamics.com", InferredRole.Test)]
    [InlineData("https://contoso-testing.crm4.dynamics.com", InferredRole.Test)]
    [InlineData("https://contoso-qa.crm4.dynamics.com", InferredRole.Test)]
    [InlineData("https://contoso-sit.crm4.dynamics.com", InferredRole.Test)]
    [InlineData("https://contoso-uat.crm4.dynamics.com", InferredRole.Uat)]
    [InlineData("https://contoso-acc.crm4.dynamics.com", InferredRole.Uat)]
    [InlineData("https://contoso-acceptance.crm4.dynamics.com", InferredRole.Uat)]
    [InlineData("https://contoso-acceptatie.crm4.dynamics.com", InferredRole.Uat)]
    [InlineData("https://contoso-preprod.crm4.dynamics.com", InferredRole.Uat)]
    [InlineData("https://contoso-staging.crm4.dynamics.com", InferredRole.Uat)]
    [InlineData("https://contoso-stage.crm4.dynamics.com", InferredRole.Uat)]
    [InlineData("https://contoso-stg.crm4.dynamics.com", InferredRole.Uat)]
    public void Infer_UrlKeyword_MapsToRole(string url, InferredRole expected)
    {
        var result = EnvironmentRoleInference.Infer(url, environmentType: "Sandbox");

        result.Role.Should().Be(expected);
        result.Source.Should().Be("URL name");
    }

    [Theory]
    [InlineData("https://dev-contoso.crm4.dynamics.com", InferredRole.Dev)]        // prefix
    [InlineData("https://contoso-dev-eu.crm4.dynamics.com", InferredRole.Dev)]     // middle
    [InlineData("https://contoso-crm-dev.crm4.dynamics.com", InferredRole.Dev)]    // suffix after another token
    [InlineData("https://contoso-dev2.crm4.dynamics.com", InferredRole.Dev)]       // trailing digits
    [InlineData("https://contoso-test01.crm4.dynamics.com", InferredRole.Test)]
    [InlineData("https://contoso-uat-3.crm4.dynamics.com", InferredRole.Uat)]      // digits as their own token
    [InlineData("https://Contoso-DEV.crm4.dynamics.com", InferredRole.Dev)]        // case
    public void Infer_UrlKeyword_AnyPosition_TrailingDigits_CaseInsensitive(string url, InferredRole expected)
    {
        EnvironmentRoleInference.Infer(url, "Sandbox").Role.Should().Be(expected);
    }

    [Theory]
    [InlineData("https://devon-crm.crm4.dynamics.com")]
    [InlineData("https://testify.crm4.dynamics.com")]
    [InlineData("https://contoso-sandbox.crm4.dynamics.com")]
    [InlineData("https://contoso-int.crm4.dynamics.com")]
    [InlineData("https://contoso-demo.crm4.dynamics.com")]
    public void Infer_NoWholeTokenMatch_ReturnsNoRole(string url)
    {
        // Whole tokens only, and only the keywords in the table: substrings and unmapped names stay uninferred.
        var result = EnvironmentRoleInference.Infer(url, "Sandbox");

        result.Role.Should().BeNull();
        result.Source.Should().BeNull();
    }

    [Fact]
    public void Infer_TwoKeywords_LastTokenWins()
    {
        // Matches the suffix position provision writes.
        EnvironmentRoleInference.Infer("https://contoso-test-uat.crm4.dynamics.com", "Sandbox").Role.Should().Be(InferredRole.Uat);
    }

    [Fact]
    public void Infer_ProductionType_BeatsUrlKeyword()
    {
        // Production is a Dataverse fact, not a naming convention: an org called contoso-test that
        // Dataverse reports as Production is PROD.
        var result = EnvironmentRoleInference.Infer("https://contoso-test.crm4.dynamics.com", "Production");

        result.Role.Should().Be(InferredRole.Prod);
        result.Source.Should().Be("environment type");
    }

    [Fact]
    public void Infer_DeveloperType_LosesToUrlKeyword()
    {
        EnvironmentRoleInference.Infer("https://contoso-test.crm4.dynamics.com", "Developer").Role.Should().Be(InferredRole.Test);
    }

    [Theory]
    [InlineData("Contoso DEV", InferredRole.Dev)]
    [InlineData("Contoso (Acceptance)", InferredRole.Uat)]
    [InlineData("Contoso Test 2", InferredRole.Test)]
    [InlineData("Contoso - QA", InferredRole.Test)]
    [InlineData("Contoso [staging]", InferredRole.Uat)]
    public void Infer_NoUrlKeyword_DisplayNameKeyword_MapsToRole(string displayName, InferredRole expected)
    {
        var result = EnvironmentRoleInference.Infer("https://contoso.crm4.dynamics.com", "Sandbox", displayName);

        result.Role.Should().Be(expected);
        result.Source.Should().Be("environment name");
    }

    [Fact]
    public void Infer_UrlKeyword_BeatsDisplayNameKeyword()
    {
        var result = EnvironmentRoleInference.Infer("https://contoso-dev.crm4.dynamics.com", "Sandbox", "Contoso UAT");

        result.Role.Should().Be(InferredRole.Dev);
        result.Source.Should().Be("URL name");
    }

    [Fact]
    public void Infer_NoKeyword_ProductionType_ReturnsProdFromType()
    {
        var result = EnvironmentRoleInference.Infer("https://contoso.crm4.dynamics.com", "Production");

        result.Role.Should().Be(InferredRole.Prod);
        result.Source.Should().Be("environment type");
    }

    [Fact]
    public void Infer_NoKeyword_DeveloperType_ReturnsDevFromType()
    {
        var result = EnvironmentRoleInference.Infer("https://contoso.crm4.dynamics.com", "Developer", "Contoso");

        result.Role.Should().Be(InferredRole.Dev);
        result.Source.Should().Be("environment type");
    }

    [Fact]
    public void Infer_NoKeyword_SandboxType_ReturnsNoRole()
    {
        // Dataverse reports test and UAT sandboxes with the same type as a DEV one — type alone can't
        // disambiguate, so a Sandbox with no keyword anywhere stays uninferred.
        var result = EnvironmentRoleInference.Infer("https://contoso.crm4.dynamics.com", "Sandbox", "Contoso");

        result.Role.Should().BeNull();
        result.Source.Should().BeNull();
    }

    [Theory]
    [InlineData("Trial")]
    [InlineData(null)]
    public void Infer_NoKeyword_UnknownOrNullType_ReturnsNoRole(string? environmentType)
    {
        EnvironmentRoleInference.Infer("https://contoso.crm4.dynamics.com", environmentType).Role.Should().BeNull();
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void Infer_UnparsableUrl_FallsThroughToNextStep_NoThrow(string? url)
    {
        var act = () => EnvironmentRoleInference.Infer(url, "Production");

        act.Should().NotThrow();
        act().Role.Should().Be(InferredRole.Prod);
    }
}
