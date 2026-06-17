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
    public void Resolve_SettingsXrmContext_NoConfig_ReturnsXrmContext()
    {
        var result = Resolve(GeneratorType.XrmContext, configGenerator: null);

        result.Should().Be(GeneratorType.XrmContext);
    }

    [Fact]
    public void Resolve_SettingsPac_NoConfig_ReturnsPac()
    {
        var result = Resolve(GeneratorType.Pac, configGenerator: null);

        result.Should().Be(GeneratorType.Pac);
    }

    [Fact]
    public void Resolve_SettingsNull_ConfigXrmContext_ReturnsXrmContext()
    {
        var result = Resolve(settingsGenerator: null, configGenerator: GeneratorType.XrmContext);

        result.Should().Be(GeneratorType.XrmContext);
    }

    [Fact]
    public void Resolve_SettingsNull_NoConfig_ReturnsPac()
    {
        var result = Resolve(settingsGenerator: null, configGenerator: null);

        result.Should().Be(GeneratorType.Pac);
    }

    [Fact]
    public void Resolve_SettingsPac_ConfigXrmContext_ReturnsPac()
    {
        // CLI flag wins over saved config
        var result = Resolve(GeneratorType.Pac, configGenerator: GeneratorType.XrmContext);

        result.Should().Be(GeneratorType.Pac);
    }
}
