using System.Text.Json;
using Flowline.Config;
using FluentAssertions;

namespace Flowline.Tests;

public class ProjectConfigTests
{
    [Fact]
    public void GetOrUpdateSolution_NewSolution_ReturnsWithName()
    {
        var config = new ProjectConfig();

        var sln = config.GetOrUpdateSolution("MySolution");

        sln!.Name.Should().Be("MySolution");
    }

    [Fact]
    public void GetOrUpdateSolution_ExistingSolution_ReturnsSameSolution()
    {
        var config = new ProjectConfig();
        config.AddOrUpdateSolution("MySolution");

        var sln = config.GetOrUpdateSolution("MySolution");

        sln!.Name.Should().Be("MySolution");
    }

    [Fact]
    public void GetOrUpdateSolution_NoName_SingleSolution_ReturnsIt()
    {
        var config = new ProjectConfig();
        config.AddOrUpdateSolution("OnlySolution");

        var sln = config.GetOrUpdateSolution(null);

        sln!.Name.Should().Be("OnlySolution");
    }

    [Fact]
    public void GetOrUpdateSolution_NoName_MultipleSolutions_ReturnsNull()
    {
        var config = new ProjectConfig();
        config.AddOrUpdateSolution("SolutionA");
        config.AddOrUpdateSolution("SolutionB");

        var sln = config.GetOrUpdateSolution(null);

        sln.Should().BeNull();
    }

    // GetOrUpdateUatUrl tests

    [Fact]
    public void GetOrUpdateUatUrl_NullInput_WhenUatUrlEmpty_ReturnsNull()
    {
        var config = new ProjectConfig();

        var result = config.GetOrUpdateUatUrl(null);

        result.Should().BeNull();
        config.UatUrl.Should().BeNull();
    }

    [Fact]
    public void GetOrUpdateUatUrl_WithUrl_WhenUatUrlEmpty_SetsAndReturnsUrl()
    {
        var config = new ProjectConfig();

        var result = config.GetOrUpdateUatUrl("https://contoso-uat.crm.dynamics.com/");

        result.Should().Be("https://contoso-uat.crm.dynamics.com/");
        config.UatUrl.Should().Be("https://contoso-uat.crm.dynamics.com/");
    }

    [Fact]
    public void GetOrUpdateUatUrl_NullInput_WhenUatUrlAlreadySet_ReturnsStoredUrl()
    {
        var config = new ProjectConfig { UatUrl = "https://contoso-uat.crm.dynamics.com/" };

        var result = config.GetOrUpdateUatUrl(null);

        result.Should().Be("https://contoso-uat.crm.dynamics.com/");
        config.UatUrl.Should().Be("https://contoso-uat.crm.dynamics.com/");
    }

    [Fact]
    public void GetOrUpdateUatUrl_SameUrl_WhenAlreadySet_ReturnsUrl()
    {
        var config = new ProjectConfig { UatUrl = "https://contoso-uat.crm.dynamics.com/" };

        var result = config.GetOrUpdateUatUrl("https://contoso-uat.crm.dynamics.com/");

        result.Should().Be("https://contoso-uat.crm.dynamics.com/");
    }

    [Fact]
    public void UatUrl_RoundTripsViaJson()
    {
        var config = new ProjectConfig { UatUrl = "https://contoso-uat.crm.dynamics.com/" };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        var restored = JsonSerializer.Deserialize<ProjectConfig>(json)!;

        restored.UatUrl.Should().Be("https://contoso-uat.crm.dynamics.com/");
    }

    // GenerateConfig tests

    [Fact]
    public void GenerateConfig_NullGenerate_RoundTrips()
    {
        var config = new ProjectConfig();
        config.AddOrUpdateSolution("MySolution");

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        var restored = JsonSerializer.Deserialize<ProjectConfig>(json)!;

        restored.Solutions.Single().Generate.Should().BeNull();
        json.Should().NotContain("Generate"); // omitted when null
    }

    [Fact]
    public void GenerateConfig_FullConfig_RoundTrips()
    {
        var config = new ProjectConfig();
        var sln = config.AddOrUpdateSolution("MySolution");
        sln.Generate = new GenerateConfig { Namespace = "A.Models", ExtraTables = ["account", "lead"] };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        var restored = JsonSerializer.Deserialize<ProjectConfig>(json)!;

        var g = restored.Solutions.Single().Generate;
        g.Should().NotBeNull();
        g!.Namespace.Should().Be("A.Models");
        g.ExtraTables.Should().BeEquivalentTo(["account", "lead"]);
    }

    [Fact]
    public void AddOrUpdateSolution_PreservesGenerate_WhenUpdatingIncludeManaged()
    {
        var config = new ProjectConfig();
        var sln = config.AddOrUpdateSolution(new ProjectSolution { Name = "MySolution", IncludeManaged = false, Generate = new GenerateConfig { Namespace = "A.Models" } });

        // Re-add same solution with IncludeManaged = true — Generate must survive
        config.AddOrUpdateSolution(new ProjectSolution { Name = "MySolution", IncludeManaged = true, Generate = sln.Generate });

        config.Solutions.Single().Generate?.Namespace.Should().Be("A.Models");
    }

    [Fact]
    public void GenerateConfig_NullExtraTables_OmittedFromJson()
    {
        var config = new GenerateConfig { Namespace = "A.Models", ExtraTables = null };

        var json = JsonSerializer.Serialize(config);

        json.Should().NotContain("ExtraTables");
    }

    [Fact]
    public void GenerateConfig_EmptyExtraTables_SerializesAsEmptyArray()
    {
        // WhenWritingNull suppresses null but not []. At the config level, [] serializes as [].
        // GenerateCommand normalizes empty input to null before storing, so [] never reaches .flowline.
        var config = new GenerateConfig { Namespace = "A.Models", ExtraTables = [] };

        var json = JsonSerializer.Serialize(config);

        json.Should().Contain("ExtraTables").And.Contain("[]");
    }

    [Fact]
    public void GenerateConfig_NamespaceOnly_ExtraTablesOmitted()
    {
        var config = new GenerateConfig { Namespace = "A.Models", ExtraTables = null };

        var json = JsonSerializer.Serialize(config);
        var restored = JsonSerializer.Deserialize<GenerateConfig>(json)!;

        restored.Namespace.Should().Be("A.Models");
        restored.ExtraTables.Should().BeNull();
        json.Should().NotContain("ExtraTables");
    }

    [Fact]
    public void GenerateConfig_ExtraTablesOnly_NamespaceOmitted()
    {
        var config = new GenerateConfig { Namespace = null, ExtraTables = ["account"] };

        var json = JsonSerializer.Serialize(config);
        var restored = JsonSerializer.Deserialize<GenerateConfig>(json)!;

        restored.ExtraTables.Should().BeEquivalentTo(["account"]);
        restored.Namespace.Should().BeNull();
        json.Should().NotContain("Namespace");
    }
}

public class GeneratorTypeSerializationTests
{
    [Fact]
    public void GenerateConfig_XrmContext3_SerializesAsEnumName()
    {
        var config = new GenerateConfig { Generator = GeneratorType.XrmContext3 };

        var json = JsonSerializer.Serialize(config);

        json.Should().Contain("\"Generator\"").And.Contain("\"XrmContext3\"");
    }

    [Fact]
    public void GenerateConfig_NullGenerator_OmittedFromJson()
    {
        var config = new GenerateConfig { Generator = null };

        var json = JsonSerializer.Serialize(config);

        json.Should().NotContain("Generator");
    }

    [Fact]
    public void GenerateConfig_PacJson_DeserializesToPac()
    {
        var json = """{"Generator":"Pac"}""";

        var config = JsonSerializer.Deserialize<GenerateConfig>(json)!;

        config.Generator.Should().Be(GeneratorType.Pac);
    }

    [Fact]
    public void GenerateConfig_XrmContext3Json_DeserializesToXrmContext3()
    {
        var json = """{"Generator":"XrmContext3"}""";

        var config = JsonSerializer.Deserialize<GenerateConfig>(json)!;

        config.Generator.Should().Be(GeneratorType.XrmContext3);
    }

    [Fact]
    public void GenerateConfig_XrmContext_SerializesAsEnumName()
    {
        var config = new GenerateConfig { Generator = GeneratorType.XrmContext };

        var json = JsonSerializer.Serialize(config);

        json.Should().Contain("\"Generator\"").And.Contain("\"XrmContext\"");
    }

    [Fact]
    public void GenerateConfig_XrmContextJson_DeserializesToXrmContext()
    {
        var json = """{"Generator":"XrmContext"}""";

        var config = JsonSerializer.Deserialize<GenerateConfig>(json)!;

        config.Generator.Should().Be(GeneratorType.XrmContext);
    }
}
