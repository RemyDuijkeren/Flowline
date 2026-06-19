using System.Text.Json;
using Flowline.Config;
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

    [Fact]
    public void Resolve_SettingsXrmContext_NoConfig_ReturnsXrmContext()
    {
        var result = Resolve(GeneratorType.XrmContext, configGenerator: null);

        result.Should().Be(GeneratorType.XrmContext);
    }

    [Fact]
    public void Resolve_SettingsNull_ConfigXrmContext_ReturnsXrmContext()
    {
        var result = Resolve(settingsGenerator: null, configGenerator: GeneratorType.XrmContext);

        result.Should().Be(GeneratorType.XrmContext);
    }
}

public class GenerateCommandEarlyValidationTests
{
    // Mirrors early validation in GenerateCommand.ExecuteAsync:
    //   if (settings.ClientId != null && settings.Secret == null) throw FlowlineException

    static void Validate(string? clientId, string? secret)
    {
        if (clientId != null && secret == null)
            throw new FlowlineException("--client-id requires --secret");
    }

    [Fact]
    public void ClientIdWithoutSecret_ThrowsFlowlineException()
    {
        var act = () => Validate("my-client-id", secret: null);

        act.Should().Throw<FlowlineException>().WithMessage("--client-id requires --secret");
    }

    [Fact]
    public void ClientIdWithSecret_NoException()
    {
        var act = () => Validate("my-client-id", "my-secret");

        act.Should().NotThrow();
    }

    [Fact]
    public void SecretAlone_NoException()
    {
        var act = () => Validate(clientId: null, "my-secret");

        act.Should().NotThrow();
    }

    [Fact]
    public void NeitherClientIdNorSecret_NoException()
    {
        var act = () => Validate(clientId: null, secret: null);

        act.Should().NotThrow();
    }
}

public class GenerateConfigJsonTests
{
    [Fact]
    public void GenerateConfig_Serialized_NoXrmClientIdOrXrmUsername()
    {
        var config = new GenerateConfig { Namespace = "A.Models", Generator = GeneratorType.XrmContext3 };

        var json = JsonSerializer.Serialize(config);

        json.Should().NotContain("XrmClientId")
            .And.NotContain("XrmUsername")
            .And.NotContain("xrmClientId")
            .And.NotContain("xrmUsername");
    }

    [Fact]
    public void GenerateConfig_WithUnknownFields_DeserializesWithoutError()
    {
        // Old .flowline files may contain xrmClientId — JSON should ignore unknown properties
        var json = """{"Namespace":"A.Models","Generator":"XrmContext3","xrmClientId":"old-id","xrmUsername":"user@contoso.com"}""";

        var act = () => JsonSerializer.Deserialize<GenerateConfig>(json);

        act.Should().NotThrow();
        var config = act();
        config!.Namespace.Should().Be("A.Models");
        config.Generator.Should().Be(GeneratorType.XrmContext3);
    }
}
