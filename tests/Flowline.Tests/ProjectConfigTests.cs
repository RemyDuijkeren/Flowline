using System.Text.Json;
using Flowline.Config;
using Flowline.Core;
using FluentAssertions;

namespace Flowline.Tests;

public class ProjectConfigTests : IDisposable
{
    readonly string _tempDir;

    public ProjectConfigTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("flowline-config-tests-").FullName;
    }

    public void Dispose()
    {
        Directory.Delete(_tempDir, recursive: true);
    }

    void WriteConfigFile(string json)
    {
        File.WriteAllText(Path.Combine(_tempDir, ".flowline"), json);
    }

    [Fact]
    public void GetOrUpdateSolution_NewSolution_ReturnsWithName()
    {
        var config = new ProjectConfig();

        var sln = config.GetOrUpdateSolution("MySolution");

        sln!.UniqueName.Should().Be("MySolution");
    }

    [Fact]
    public void GetOrUpdateSolution_ExistingSolution_ReturnsSameSolution()
    {
        var config = new ProjectConfig();
        config.AddOrUpdateSolution("MySolution");

        var sln = config.GetOrUpdateSolution("MySolution");

        sln!.UniqueName.Should().Be("MySolution");
    }

    [Fact]
    public void GetOrUpdateSolution_NoName_SingleSolution_ReturnsIt()
    {
        var config = new ProjectConfig();
        config.AddOrUpdateSolution("OnlySolution");

        var sln = config.GetOrUpdateSolution(null);

        sln!.UniqueName.Should().Be("OnlySolution");
    }

    [Fact]
    public void GetOrUpdateSolution_NoName_NoSolution_ReturnsNull()
    {
        var config = new ProjectConfig();

        var sln = config.GetOrUpdateSolution(null);

        sln.Should().BeNull();
    }

    // Regression: the no-name/single-solution shortcut used to return the resolved solution
    // immediately, bypassing the includeManaged conflict check below it entirely — so
    // `sync --managed false` on a single-solution project (the common case: no positional
    // solution name passed) silently kept the old IncludeManaged value with no prompt, no
    // warning, no update.
    [Fact]
    public void GetOrUpdateSolution_NoName_SingleSolution_IncludeManagedDiffers_ForceConfig_Updates()
    {
        var config = new ProjectConfig();
        config.AddOrUpdateSolution("OnlySolution", includeManaged: true);
        var settings = new FlowlineSettings { Force = ["config"] };

        var sln = config.GetOrUpdateSolution(null, includeManaged: false, settings);

        sln!.IncludeManaged.Should().BeFalse();
        config.Solution!.IncludeManaged.Should().BeFalse();
    }

    [Fact]
    public void GetOrUpdateSolution_NoName_SingleSolution_IncludeManagedDiffers_NoForce_ThrowsForceRequired()
    {
        var config = new ProjectConfig();
        config.AddOrUpdateSolution("OnlySolution", includeManaged: true);

        var act = () => config.GetOrUpdateSolution(null, includeManaged: false, new FlowlineSettings());

        act.Should().Throw<FlowlineException>().Where(e => e.ExitCode == ExitCode.ForceRequired);
        config.Solution!.IncludeManaged.Should().BeTrue();
    }

    [Fact]
    public void GetOrUpdateSolution_NoName_SingleSolution_IncludeManagedMatches_NoPromptNeeded()
    {
        var config = new ProjectConfig();
        config.AddOrUpdateSolution("OnlySolution", includeManaged: false);

        var sln = config.GetOrUpdateSolution(null, includeManaged: false, new FlowlineSettings());

        sln!.IncludeManaged.Should().BeFalse();
    }

    // Regression: the single-object collapse dropped the by-name identity check the old
    // HashSet lookup provided implicitly (a name not found used to create a new entry, not
    // silently resolve to a different existing one). Without this check, `flowline clone
    // SolutionB` on a project already configured for SolutionA would silently keep SolutionA
    // and report success, ignoring the requested name entirely -- found during code review.
    [Fact]
    public void GetOrUpdateSolution_MismatchedName_ThrowsValidationFailed()
    {
        var config = new ProjectConfig();
        config.AddOrUpdateSolution("SolutionA");

        var act = () => config.GetOrUpdateSolution("SolutionB");

        act.Should().Throw<FlowlineException>().Where(e => e.ExitCode == ExitCode.ValidationFailed);
        config.Solution!.UniqueName.Should().Be("SolutionA");
    }

    [Fact]
    public void GetOrUpdateSolution_MatchingNameCaseInsensitive_DoesNotThrow()
    {
        var config = new ProjectConfig();
        config.AddOrUpdateSolution("SolutionA");

        var sln = config.GetOrUpdateSolution("solutiona");

        sln!.UniqueName.Should().Be("SolutionA");
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

        restored.Solution!.Generate.Should().BeNull();
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

        var g = restored.Solution!.Generate;
        g.Should().NotBeNull();
        g!.Namespace.Should().Be("A.Models");
        g.ExtraTables.Should().BeEquivalentTo(["account", "lead"]);
    }

    [Fact]
    public void PluginPackageMode_Dll_RoundTripsViaJsonAsString()
    {
        var config = new ProjectConfig();
        var sln = config.AddOrUpdateSolution("MySolution");
        sln.PluginPackageMode = PluginPackageMode.Dll;

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        var restored = JsonSerializer.Deserialize<ProjectConfig>(json)!;

        json.Should().Contain("\"Dll\"");
        restored.Solution!.PluginPackageMode.Should().Be(PluginPackageMode.Dll);
    }

    [Fact]
    public void PluginPackageMode_MissingFromJson_DefaultsToAuto()
    {
        var json = """{"SchemaVersion":1,"Solution":{"UniqueName":"MySolution"}}""";

        var config = JsonSerializer.Deserialize<ProjectConfig>(json)!;

        config.Solution!.PluginPackageMode.Should().Be(PluginPackageMode.Auto);
    }

    [Fact]
    public void AddOrUpdateSolution_PreservesGenerate_WhenUpdatingIncludeManaged()
    {
        var config = new ProjectConfig();
        var sln = config.AddOrUpdateSolution(new ProjectSolution { UniqueName = "MySolution", IncludeManaged = false, Generate = new GenerateConfig { Namespace = "A.Models" } });

        // Re-add same solution with IncludeManaged = true — Generate must survive
        config.AddOrUpdateSolution(new ProjectSolution { UniqueName = "MySolution", IncludeManaged = true, Generate = sln.Generate });

        config.Solution!.Generate?.Namespace.Should().Be("A.Models");
    }

    [Fact]
    public void AddOrUpdateSolution_PreservesPluginPackageMode_WhenCallerThreadsItThrough()
    {
        // Regression guard: AddOrUpdateSolution's normalizedSolution construction must copy every
        // ProjectSolution field, not just UniqueName/IncludeManaged/Generate — otherwise even a caller
        // that explicitly threads the prior value through (the same pattern the existing Generate test
        // above uses) would still lose it.
        var config = new ProjectConfig();
        var sln = config.AddOrUpdateSolution(new ProjectSolution { UniqueName = "MySolution", IncludeManaged = false, PluginPackageMode = PluginPackageMode.Dll });

        config.AddOrUpdateSolution(new ProjectSolution { UniqueName = "MySolution", IncludeManaged = true, PluginPackageMode = sln.PluginPackageMode });

        config.Solution!.PluginPackageMode.Should().Be(PluginPackageMode.Dll);
    }

    [Fact]
    public void AddOrUpdateSolution_StringOverload_PreservesPluginPackageMode()
    {
        var config = new ProjectConfig();
        config.AddOrUpdateSolution(new ProjectSolution { UniqueName = "MySolution", PluginPackageMode = PluginPackageMode.Nupkg });

        // The (string, bool) overload re-adds by name only — it must still preserve the existing
        // solution's PluginPackageMode rather than defaulting it to Auto.
        config.AddOrUpdateSolution("MySolution", includeManaged: true);

        config.Solution!.PluginPackageMode.Should().Be(PluginPackageMode.Nupkg);
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

    // Load() schema-validation tests (U1: SchemaVersion + collapsed Solution)

    [Fact]
    public void Load_ValidV1Config_FullSolution_RoundTripsEveryField()
    {
        WriteConfigFile("""
            {
              "SchemaVersion": 1,
              "ProdUrl": "https://contoso.crm.dynamics.com/",
              "Solution": {
                "UniqueName": "MySolution",
                "IncludeManaged": true,
                "PluginPackageMode": "Nupkg",
                "Generate": {
                  "Namespace": "A.Models",
                  "ExtraTables": ["account", "lead"]
                }
              }
            }
            """);

        var config = ProjectConfig.Load(_tempDir);

        config.Should().NotBeNull();
        config!.SchemaVersion.Should().Be(1);
        config.ProdUrl.Should().Be("https://contoso.crm.dynamics.com/");
        config.Solution.Should().NotBeNull();
        config.Solution!.UniqueName.Should().Be("MySolution");
        config.Solution.IncludeManaged.Should().BeTrue();
        config.Solution.PluginPackageMode.Should().Be(PluginPackageMode.Nupkg);
        config.Solution.Generate.Should().NotBeNull();
        config.Solution.Generate!.Namespace.Should().Be("A.Models");
        config.Solution.Generate.ExtraTables.Should().BeEquivalentTo(["account", "lead"]);
    }

    [Fact]
    public void Load_SchemaV1_NoSolution_LoadsSuccessfully()
    {
        // Post-provision, pre-clone bootstrap state: only URL fields saved, Solution absent.
        WriteConfigFile("""{"SchemaVersion":1,"DevUrl":"https://contoso-dev.crm.dynamics.com/"}""");

        var config = ProjectConfig.Load(_tempDir);

        config.Should().NotBeNull();
        config!.Solution.Should().BeNull();
        config.DevUrl.Should().Be("https://contoso-dev.crm.dynamics.com/");
    }

    [Fact]
    public void Load_NoConfigFile_ReturnsNull()
    {
        var config = ProjectConfig.Load(_tempDir);

        config.Should().BeNull();
    }

    [Theory]
    [InlineData("""{"SchemaVersion":1,"Solution":{"UniqueName":""}}""")]
    [InlineData("""{"SchemaVersion":1,"Solution":{"UniqueName":"   "}}""")]
    [InlineData("""{"SchemaVersion":1,"Solution":{}}""")]
    public void Load_SolutionWithEmptyOrMissingUniqueName_ThrowsConfigInvalid(string json)
    {
        WriteConfigFile(json);

        var act = () => ProjectConfig.Load(_tempDir);

        act.Should().Throw<FlowlineException>().Where(e => e.ExitCode == ExitCode.ConfigInvalid);
    }

    [Fact]
    public void Load_LegacySolutionsArray_ThrowsConfigInvalid()
    {
        WriteConfigFile("""{"Solutions":[{"Name":"MySolution"}]}""");

        var act = () => ProjectConfig.Load(_tempDir);

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ConfigInvalid && e.Message.Contains("flowline clone"));
    }

    [Fact]
    public void Load_LegacySolutionsArray_WithSchemaVersion_StillThrowsConfigInvalid()
    {
        WriteConfigFile("""{"SchemaVersion":1,"Solutions":[{"Name":"MySolution"}]}""");

        var act = () => ProjectConfig.Load(_tempDir);

        act.Should().Throw<FlowlineException>().Where(e => e.ExitCode == ExitCode.ConfigInvalid);
    }

    [Fact]
    public void Load_MissingSchemaVersion_ThrowsConfigInvalid()
    {
        WriteConfigFile("""{"ProdUrl":"https://contoso.crm.dynamics.com/"}""");

        var act = () => ProjectConfig.Load(_tempDir);

        act.Should().Throw<FlowlineException>().Where(e => e.ExitCode == ExitCode.ConfigInvalid);
    }

    [Fact]
    public void Load_UnsupportedSchemaVersion_ThrowsConfigInvalid()
    {
        WriteConfigFile("""{"SchemaVersion":2,"Solution":{"UniqueName":"MySolution"}}""");

        var act = () => ProjectConfig.Load(_tempDir);

        act.Should().Throw<FlowlineException>().Where(e => e.ExitCode == ExitCode.ConfigInvalid);
    }

    [Fact]
    public void Load_SchemaVersionAsString_ThrowsConfigInvalid()
    {
        WriteConfigFile("""{"SchemaVersion":"1","Solution":{"UniqueName":"MySolution"}}""");

        var act = () => ProjectConfig.Load(_tempDir);

        act.Should().Throw<FlowlineException>().Where(e => e.ExitCode == ExitCode.ConfigInvalid);
    }

    [Fact]
    public void Load_ExplicitNullSolution_LoadsSuccessfully()
    {
        WriteConfigFile("""{"SchemaVersion":1,"Solution":null}""");

        var config = ProjectConfig.Load(_tempDir);

        config.Should().NotBeNull();
        config!.Solution.Should().BeNull();
    }

    [Fact]
    public void Load_NonIntegerSchemaVersion_ThrowsConfigInvalid_NotFormatException()
    {
        // Regression: JsonElement.GetInt32() throws FormatException on a numeric-but-non-integer
        // token (e.g. 1.0) -- found during code review. TryGetInt32 must be used instead so this
        // fails closed with ConfigInvalid rather than crashing with a raw, unhandled exception.
        WriteConfigFile("""{"SchemaVersion":1.0,"Solution":{"UniqueName":"MySolution"}}""");

        var act = () => ProjectConfig.Load(_tempDir);

        act.Should().Throw<FlowlineException>().Where(e => e.ExitCode == ExitCode.ConfigInvalid);
    }

    [Theory]
    [InlineData("""{"SchemaVersion":1,"Solution":"MySolution"}""")]
    [InlineData("""{"SchemaVersion":1,"Solution":42}""")]
    [InlineData("""{"SchemaVersion":1,"Solution":["MySolution"]}""")]
    public void Load_SolutionNotAnObject_ThrowsConfigInvalid_NotInvalidOperationException(string json)
    {
        // Regression: JsonElement.TryGetProperty throws InvalidOperationException when called on a
        // non-object element -- found during code review. A non-null Solution that isn't a JSON
        // object must fail closed with ConfigInvalid before any TryGetProperty call on it.
        WriteConfigFile(json);

        var act = () => ProjectConfig.Load(_tempDir);

        act.Should().Throw<FlowlineException>().Where(e => e.ExitCode == ExitCode.ConfigInvalid);
    }

    [Fact]
    public void Load_MalformedJson_ThrowsConfigInvalid_NotSilentNull()
    {
        WriteConfigFile("{ this is not valid json");

        var act = () => ProjectConfig.Load(_tempDir);

        act.Should().Throw<FlowlineException>().Where(e => e.ExitCode == ExitCode.ConfigInvalid);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"foo\"")]
    public void Load_ValidJsonButNotAnObject_ThrowsConfigInvalid(string json)
    {
        // Parseable JSON whose root isn't an object (array/number/string) must fail closed too —
        // TryGetProperty on a non-object JsonElement throws InvalidOperationException otherwise.
        WriteConfigFile(json);

        var act = () => ProjectConfig.Load(_tempDir);

        act.Should().Throw<FlowlineException>().Where(e => e.ExitCode == ExitCode.ConfigInvalid);
    }

    [Fact]
    public void Save_StampsSchemaVersion1_EvenWithNullSolution()
    {
        // provision saves .flowline with only URL fields, before any solution is cloned.
        var config = new ProjectConfig { DevUrl = "https://contoso-dev.crm.dynamics.com/" };

        config.Save(_tempDir);
        var reloaded = ProjectConfig.Load(_tempDir);

        reloaded.Should().NotBeNull();
        reloaded!.SchemaVersion.Should().Be(1);
        reloaded.Solution.Should().BeNull();
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
