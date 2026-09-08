using Flowline.Core.Services;
using FluentAssertions;

namespace Flowline.Core.Tests;

public class EnvironmentRoleInferenceTests
{
    [Theory]
    [InlineData("https://contoso-dev.crm4.dynamics.com", InferredRole.Dev)]
    [InlineData("https://contoso-test.crm4.dynamics.com", InferredRole.Test)]
    [InlineData("https://contoso-uat.crm4.dynamics.com", InferredRole.Uat)]
    public void Infer_HostSuffix_WinsOverType(string url, InferredRole expected)
    {
        // Type deliberately contradicts the suffix, to prove suffix wins (R8: first hit wins).
        var result = EnvironmentRoleInference.Infer(url, environmentType: "Production");

        result.Role.Should().Be(expected);
        result.Source.Should().Be("URL suffix");
    }

    [Fact]
    public void Infer_NoSuffix_ProductionType_ReturnsProdFromType()
    {
        var result = EnvironmentRoleInference.Infer("https://contoso.crm4.dynamics.com", "Production");

        result.Role.Should().Be(InferredRole.Prod);
        result.Source.Should().Be("environment type");
    }

    [Fact]
    public void Infer_NoSuffix_DeveloperType_ReturnsDevFromType()
    {
        var result = EnvironmentRoleInference.Infer("https://contoso.crm4.dynamics.com", "Developer");

        result.Role.Should().Be(InferredRole.Dev);
        result.Source.Should().Be("environment type");
    }

    [Fact]
    public void Infer_NoSuffix_SandboxType_ReturnsNoRole()
    {
        // Dataverse reports test and UAT sandboxes with the same type as a DEV one — type alone can't
        // disambiguate, so a suffix-less Sandbox stays uninferred.
        var result = EnvironmentRoleInference.Infer("https://contoso.crm4.dynamics.com", "Sandbox");

        result.Role.Should().BeNull();
        result.Source.Should().BeNull();
    }

    [Fact]
    public void Infer_NoSuffix_UnknownType_ReturnsNoRole()
    {
        var result = EnvironmentRoleInference.Infer("https://contoso.crm4.dynamics.com", "Trial");

        result.Role.Should().BeNull();
    }

    [Fact]
    public void Infer_NoSuffix_NullType_ReturnsNoRole()
    {
        var result = EnvironmentRoleInference.Infer("https://contoso.crm4.dynamics.com", null);

        result.Role.Should().BeNull();
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void Infer_UnparsableUrl_FallsThroughToTypeStep_NoThrow(string? url)
    {
        var act = () => EnvironmentRoleInference.Infer(url, "Production");

        act.Should().NotThrow();
        act().Role.Should().Be(InferredRole.Prod);
    }

    [Fact]
    public void Infer_SuffixCaseInsensitive()
    {
        var result = EnvironmentRoleInference.Infer("https://Contoso-DEV.crm4.dynamics.com", null);

        result.Role.Should().Be(InferredRole.Dev);
    }
}
