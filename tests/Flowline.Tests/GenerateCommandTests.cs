using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Flowline.Commands;
using Flowline.Config;
using Flowline.Core;
using FluentAssertions;
using Spectre.Console.Cli;

namespace Flowline.Tests;

public class GenerateCommandParseTests
{
    // -- R1/R4: --env replaces --dev (U3) — generate accepts any role, incl. Production (KTD8) --

    sealed class CapturingGenerateSettingsCommand : Command<GenerateCommand.Settings>
    {
        public static Action<GenerateCommand.Settings>? OnExecute;

        protected override int Execute(CommandContext context, GenerateCommand.Settings settings, CancellationToken cancellationToken)
        {
            OnExecute?.Invoke(settings);
            return 0;
        }
    }

    [Fact]
    public void CommandApp_EnvRoleKeyword_BindsEnv()
    {
        GenerateCommand.Settings? captured = null;
        var app = new CommandApp();
        app.Configure(c => c.AddCommand<CapturingGenerateSettingsCommand>("generate"));
        CapturingGenerateSettingsCommand.OnExecute = s => captured = s;

        var exitCode = app.Run(["generate", "--env", "prod"]);

        exitCode.Should().Be(0);
        captured!.Env.Should().Be("prod");
    }

    [Fact]
    public void Settings_HasNoDevUrlProperty_TheOldFlagIsGone()
    {
        typeof(GenerateCommand.Settings).GetProperty("DevUrl").Should().BeNull();
    }
}

public class GeneratorResolutionTests
{
    // Mirrors the resolution expression in GenerateCommand.ExecuteFlowlineAsync:
    //   var resolvedGeneratorType = projectSln?.Generate?.Generator ?? settings.Generator ?? GeneratorType.Pac;
    // Config-first (U5/R11): ApplyPersistedGenerateSettings already merges a passed --generator into
    // projectSln.Generate before this line runs, so config reflects the winning value whenever both are
    // present — except a declined interactive overwrite, where config correctly keeps the old value
    // instead of the declined one. settingsGenerator only matters here for standalone mode, where there's
    // no projectSln to merge into.
    static GeneratorType Resolve(GeneratorType? settingsGenerator, GeneratorType? configGenerator)
    {
        var projectSln = configGenerator.HasValue
            ? new ProjectSolution { UniqueName = "Test", Generate = new GenerateConfig { Generator = configGenerator } }
            : null;

        // Replicate the expression directly
        return projectSln?.Generate?.Generator ?? settingsGenerator ?? GeneratorType.Pac;
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
    public void Resolve_SettingsPac_ConfigXrmContext3_ReturnsConfigValue()
    {
        // Both present: config wins at this line. In the real command this only happens after a
        // declined interactive overwrite prompt — ApplyPersistedGenerateSettings otherwise merges the
        // flag into config first, so config already equals the flag by the time this expression runs.
        var result = Resolve(GeneratorType.Pac, configGenerator: GeneratorType.XrmContext3);

        result.Should().Be(GeneratorType.XrmContext3);
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
    //   if (settings.ClientId != null && settings.ClientSecret == null) throw FlowlineException

    static void Validate(string? clientId, string? clientSecret)
    {
        if (clientId != null && clientSecret == null)
            throw new FlowlineException("--client-id requires --client-secret");
    }

    [Fact]
    public void ClientIdWithoutSecret_ThrowsFlowlineException()
    {
        var act = () => Validate("my-client-id", clientSecret: null);

        act.Should().Throw<FlowlineException>().WithMessage("--client-id requires --client-secret");
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
        var act = () => Validate(clientId: null, clientSecret: null);

        act.Should().NotThrow();
    }
}

public class GeneratePersistSettingsTests
{
    static readonly ProjectSolution s_solution = new() { UniqueName = "ContosoSales" };

    [Fact]
    public void ProjectMode_WithConfiguredSolution_Persists()
    {
        GenerateCommand.ShouldPersistSettings(standaloneMode: false, s_solution).Should().BeTrue();
    }

    [Fact]
    public void StandaloneMode_DoesNotPersist()
    {
        GenerateCommand.ShouldPersistSettings(standaloneMode: true, s_solution).Should().BeFalse();
    }

    [Fact]
    public void ProjectMode_WithoutConfiguredSolution_DoesNotPersist()
    {
        GenerateCommand.ShouldPersistSettings(standaloneMode: false, projectSln: null).Should().BeFalse();
    }
}

public class GenerateFileSafetyTests : IDisposable
{
    private readonly string _tempDir;

    public GenerateFileSafetyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // Mirrors IsGeneratorOwned from GenerateCommand
    static bool IsGeneratorOwned(string filePath) =>
        File.ReadLines(filePath).Take(15).Any(line =>
            line.Contains("<auto-generated>") || line.Contains("GeneratedCode("));

    // Mirrors the merge step from GenerateCommand swap block
    static void MergeUserFiles(string modelsFolder, string tempFolder)
    {
        if (!Directory.Exists(modelsFolder)) return;
        var generatorPaths = Directory.EnumerateFiles(tempFolder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(tempFolder, f))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(modelsFolder, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(modelsFolder, file);
            if (generatorPaths.Contains(rel) || IsGeneratorOwned(file)) continue;
            var dest = Path.Combine(tempFolder, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
    }

    string WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    const string PacHeader = """
        #pragma warning disable CS1591
        //------------------------------------------------------------------------------
        // <auto-generated>
        //     This code was generated by a tool.
        //
        //     Changes to this file may cause incorrect behavior and will be lost if
        //     the code is regenerated.
        // </auto-generated>
        //------------------------------------------------------------------------------
        namespace A.Models;
        """;

    const string XrmContextHeader = """
        //------------------------------------------------------------------------------
        // <auto-generated>
        //     This code was generated by a tool.
        //     Runtime Version:4.0.30319.42000
        //
        //     Changes to this file may cause incorrect behavior and will be lost if
        //     the code is regenerated.
        // </auto-generated>
        //------------------------------------------------------------------------------
        namespace A.Models;
        """;

    // xrmcontext v4 (DataverseProxyGenerator) — no <auto-generated>, uses GeneratedCode attribute
    const string XrmContext4Header = """
        namespace A.Models;
        [System.CodeDom.Compiler.GeneratedCode("DataverseProxyGenerator", "4.0.0.25")]
        public partial class Account { }
        """;

    // --- IsGeneratorOwned ---

    [Fact]
    public void IsGeneratorOwned_PacHeader_ReturnsTrue()
    {
        var path = WriteFile("Account.cs", PacHeader);

        IsGeneratorOwned(path).Should().BeTrue();
    }

    [Fact]
    public void IsGeneratorOwned_XrmContextHeader_ReturnsTrue()
    {
        var path = WriteFile("Account.cs", XrmContextHeader);

        IsGeneratorOwned(path).Should().BeTrue();
    }

    [Fact]
    public void IsGeneratorOwned_XrmContext4Header_ReturnsTrue()
    {
        var path = WriteFile("Account.cs", XrmContext4Header);

        IsGeneratorOwned(path).Should().BeTrue();
    }

    [Fact]
    public void IsGeneratorOwned_PlainCsFile_ReturnsFalse()
    {
        var path = WriteFile("MyPartial.cs", "namespace A; public partial class Account { }");

        IsGeneratorOwned(path).Should().BeFalse();
    }

    [Fact]
    public void IsGeneratorOwned_CsprojFile_ReturnsFalse()
    {
        var path = WriteFile("Plugins.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        IsGeneratorOwned(path).Should().BeFalse();
    }

    [Fact]
    public void IsGeneratorOwned_MarkdownFile_ReturnsFalse()
    {
        var path = WriteFile("README.md", "# Models\nHand-written docs.");

        IsGeneratorOwned(path).Should().BeFalse();
    }

    [Fact]
    public void IsGeneratorOwned_BinaryFile_ReturnsFalse()
    {
        var path = Path.Combine(_tempDir, "lib.dll");
        File.WriteAllBytes(path, [0x4D, 0x5A, 0x00, 0x01, 0x02, 0xFF, 0xFE, 0x00]);

        IsGeneratorOwned(path).Should().BeFalse();
    }

    [Fact]
    public void IsGeneratorOwned_MarkerBeyondLine15_ReturnsFalse()
    {
        var lines = Enumerable.Repeat("// padding", 16).ToList();
        lines[15] = "// <auto-generated>";
        var path = WriteFile("Late.cs", string.Join(Environment.NewLine, lines));

        IsGeneratorOwned(path).Should().BeFalse();
    }

    // --- Merge step ---

    [Fact]
    public void MergeUserFiles_UserCsproj_CopiedToTemp()
    {
        var models = Path.Combine(_tempDir, "Models");
        var temp = Path.Combine(_tempDir, "Models~");
        Directory.CreateDirectory(models);
        Directory.CreateDirectory(temp);
        File.WriteAllText(Path.Combine(models, "Plugins.csproj"), "<Project />");

        MergeUserFiles(models, temp);

        File.Exists(Path.Combine(temp, "Plugins.csproj")).Should().BeTrue();
    }

    [Fact]
    public void MergeUserFiles_GeneratedFile_NotCopiedToTemp()
    {
        var models = Path.Combine(_tempDir, "Models");
        var temp = Path.Combine(_tempDir, "Models~");
        Directory.CreateDirectory(models);
        Directory.CreateDirectory(temp);
        File.WriteAllText(Path.Combine(models, "Account.cs"), PacHeader);

        MergeUserFiles(models, temp);

        File.Exists(Path.Combine(temp, "Account.cs")).Should().BeFalse();
    }

    [Fact]
    public void MergeUserFiles_UserFileCollidesWithGeneratorOutput_GeneratorVersionKept()
    {
        var models = Path.Combine(_tempDir, "Models");
        var temp = Path.Combine(_tempDir, "Models~");
        Directory.CreateDirectory(models);
        Directory.CreateDirectory(temp);
        File.WriteAllText(Path.Combine(models, "Account.cs"), "// user partial");
        File.WriteAllText(Path.Combine(temp, "Account.cs"), PacHeader);

        MergeUserFiles(models, temp);

        File.ReadAllText(Path.Combine(temp, "Account.cs")).Should().Be(PacHeader);
    }

    [Fact]
    public void MergeUserFiles_NoHeaderFileInGeneratorOutput_GeneratorVersionKeptNoException()
    {
        // XrmExtensions.cs scenario: generator-produced file without <auto-generated> header
        var models = Path.Combine(_tempDir, "Models");
        var temp = Path.Combine(_tempDir, "Models~");
        Directory.CreateDirectory(models);
        Directory.CreateDirectory(temp);
        File.WriteAllText(Path.Combine(models, "XrmExtensions.cs"), "// old version, no header");
        File.WriteAllText(Path.Combine(temp, "XrmExtensions.cs"), "// fresh generator output");

        var act = () => MergeUserFiles(models, temp);

        act.Should().NotThrow();
        File.ReadAllText(Path.Combine(temp, "XrmExtensions.cs")).Should().Be("// fresh generator output");
    }

    [Fact]
    public void MergeUserFiles_UserFileInSubdirectory_CopiedWithRelativePath()
    {
        var models = Path.Combine(_tempDir, "Models");
        var temp = Path.Combine(_tempDir, "Models~");
        Directory.CreateDirectory(temp);
        WriteFile(Path.Combine("Models", "Entities", "MyPartial.cs"), "namespace A; public partial class Account { }");

        MergeUserFiles(models, temp);

        File.Exists(Path.Combine(temp, "Entities", "MyPartial.cs")).Should().BeTrue();
    }

    [Fact]
    public void MergeUserFiles_NoModelsFolder_DoesNotThrow()
    {
        var models = Path.Combine(_tempDir, "Models");
        var temp = Path.Combine(_tempDir, "Models~");
        Directory.CreateDirectory(temp);

        var act = () => MergeUserFiles(models, temp);

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

public class GenerateCommandForceTests
{
    [Fact]
    public void ValidateForce_UnrecognizedValue_ThrowsNamingConfigAndAll()
    {
        var settings = new GenerateCommand.Settings { Force = ["dirty"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, FlowlineSettings.ConfigOnlyValidSpecifiers, "generate");

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ValidationFailed && e.Message.Contains("config") && e.Message.Contains("all"));
    }

    [Fact]
    public void ValidateForce_Config_DoesNotThrow()
    {
        var settings = new GenerateCommand.Settings { Force = ["config"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, FlowlineSettings.ConfigOnlyValidSpecifiers, "generate");

        act.Should().NotThrow();
    }
}

public class GenerateCommandSolutionValidationTests
{
    // R8: project mode's positional [solution] validated against the single configured solution.

    [Fact]
    public void ValidateSolutionMatchesConfig_NoInputName_DoesNotThrow()
    {
        var act = () => GenerateCommand.ValidateSolutionMatchesConfig(null, "ContosoCustomizations");

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateSolutionMatchesConfig_MatchingNameCaseInsensitive_DoesNotThrow()
    {
        var act = () => GenerateCommand.ValidateSolutionMatchesConfig("contosocustomizations", "ContosoCustomizations");

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateSolutionMatchesConfig_MatchingNameWithWhitespace_DoesNotThrow()
    {
        var act = () => GenerateCommand.ValidateSolutionMatchesConfig("  ContosoCustomizations  ", "ContosoCustomizations");

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateSolutionMatchesConfig_MismatchedName_Throws()
    {
        var act = () => GenerateCommand.ValidateSolutionMatchesConfig("OtherSolution", "ContosoCustomizations");

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ValidationFailed
                && e.Message.Contains("OtherSolution") && e.Message.Contains("ContosoCustomizations"));
    }
}

// U5/R11: --namespace, --extra-tables, --generator, --output and --service-context-name each follow
// the one write-on-first-use, ask-on-change rule via GenerateCommand.ApplyPersistedGenerateSettings.
// Shares a collection with ProjectConfigTests — both swap the static AnsiConsole.Console via
// ProjectConfigTests.WithSwappedConsole, and an unscoped pair races across parallel test classes.
[Collection("ProjectConfigConsole")]
public class GeneratePersistedSettingsApplicationTests
{
    const string RootFolder = @"C:\proj";

    static ProjectSolution NewSolution(GenerateConfig? generate = null) =>
        new() { UniqueName = "ContosoSales", Generate = generate };

    [Fact]
    public void Namespace_EmptyKey_SavesAndPrints()
    {
        var sln = NewSolution();
        var settings = new GenerateCommand.Settings { Namespace = "Contoso.Models" };

        var output = ProjectConfigTests.WithSwappedConsole(console =>
        {
            GenerateCommand.ApplyPersistedGenerateSettings(sln, settings, RootFolder);
            return console.Output;
        });

        output.Should().Contain("Saved to .flowline: Solution.Generate.Namespace");
        sln.Generate!.Namespace.Should().Be("Contoso.Models");
    }

    [Fact]
    public void Namespace_SameValue_PrintsNothing()
    {
        var sln = NewSolution(new GenerateConfig { Namespace = "Contoso.Models" });
        var settings = new GenerateCommand.Settings { Namespace = "Contoso.Models" };

        var output = ProjectConfigTests.WithSwappedConsole(console =>
        {
            GenerateCommand.ApplyPersistedGenerateSettings(sln, settings, RootFolder);
            return console.Output;
        });

        output.Should().BeEmpty();
        sln.Generate!.Namespace.Should().Be("Contoso.Models");
    }

    [Fact]
    public void ExtraTables_DifferentValue_NonInteractive_NoForce_ThrowsForceRequired()
    {
        var sln = NewSolution(new GenerateConfig { ExtraTables = ["account", "contact"] });
        var settings = new GenerateCommand.Settings { ExtraTables = "contact" };

        var act = () => GenerateCommand.ApplyPersistedGenerateSettings(sln, settings, RootFolder);

        act.Should().Throw<FlowlineException>().Where(e => e.ExitCode == ExitCode.ForceRequired
            && e.Message.Contains("--force config"));
        sln.Generate!.ExtraTables.Should().BeEquivalentTo(["account", "contact"]);
    }

    [Fact]
    public void ExtraTables_DifferentValue_ForceConfig_ReplacesList()
    {
        var sln = NewSolution(new GenerateConfig { ExtraTables = ["account", "contact"] });
        var settings = new GenerateCommand.Settings { ExtraTables = "contact", Force = ["config"] };

        GenerateCommand.ApplyPersistedGenerateSettings(sln, settings, RootFolder);

        sln.Generate!.ExtraTables.Should().BeEquivalentTo(["contact"]);
    }

    [Fact]
    public void ExtraTables_SameValue_PrintsNothingAndKeepsList()
    {
        var sln = NewSolution(new GenerateConfig { ExtraTables = ["account", "contact"] });
        var settings = new GenerateCommand.Settings { ExtraTables = "account,contact" };

        var output = ProjectConfigTests.WithSwappedConsole(console =>
        {
            GenerateCommand.ApplyPersistedGenerateSettings(sln, settings, RootFolder);
            return console.Output;
        });

        output.Should().BeEmpty();
        sln.Generate!.ExtraTables.Should().BeEquivalentTo(["account", "contact"]);
    }

    [Fact]
    public void Generator_EmptyKey_SavesValue()
    {
        var sln = NewSolution();
        var settings = new GenerateCommand.Settings { Generator = GeneratorType.XrmContext3 };

        GenerateCommand.ApplyPersistedGenerateSettings(sln, settings, RootFolder);

        sln.Generate!.Generator.Should().Be(GeneratorType.XrmContext3);
    }

    [Fact]
    public void Output_EmptyKey_SavesPathRelativeToRoot()
    {
        var sln = NewSolution();
        var settings = new GenerateCommand.Settings { Output = Path.Combine(RootFolder, "src", "Models") };

        GenerateCommand.ApplyPersistedGenerateSettings(sln, settings, RootFolder);

        sln.Generate!.OutputPath.Should().Be(Path.Combine("src", "Models"));
    }

    [Fact]
    public void AbsentFlags_LeaveGenerateConfigUntouched()
    {
        var sln = NewSolution(new GenerateConfig { Namespace = "Existing.Models", Generator = GeneratorType.XrmContext });
        var settings = new GenerateCommand.Settings();

        GenerateCommand.ApplyPersistedGenerateSettings(sln, settings, RootFolder);

        sln.Generate!.Namespace.Should().Be("Existing.Models");
        sln.Generate!.Generator.Should().Be(GeneratorType.XrmContext);
    }
}

// R12: every persisting flag's [Description] ends with the same wording.
public class GeneratePersistingFlagDescriptionTests
{
    [Theory]
    [InlineData(nameof(GenerateCommand.Settings.Namespace))]
    [InlineData(nameof(GenerateCommand.Settings.ExtraTables))]
    [InlineData(nameof(GenerateCommand.Settings.Generator))]
    [InlineData(nameof(GenerateCommand.Settings.Output))]
    [InlineData(nameof(GenerateCommand.Settings.ServiceContextName))]
    [InlineData(nameof(GenerateCommand.Settings.Env))]
    public void PersistingFlag_DescriptionEndsWithSavedToFlowline(string propertyName)
    {
        var description = typeof(GenerateCommand.Settings).GetProperty(propertyName)!
            .GetCustomAttribute<DescriptionAttribute>()!;

        description.Description.Should().EndWith("(saved to .flowline)");
    }
}
