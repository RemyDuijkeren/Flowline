using FluentAssertions;
using Flowline.Generators;

namespace Flowline.Tests.Generators;

public class EbgGeneratorTests
{
    const string Namespace = "MySolution.Models";
    const string TempOutputPath = @"C:\solutions\MySolution\Plugins\Models~";
    const string SettingsPath = @"C:\temp\flowline-ebg-1\builderSettings.json";
    const string WorkDir = @"C:\temp\flowline-ebg-1";

    static DLaB.EarlyBoundGeneratorV2.Settings.EarlyBoundGeneratorConfig Build(
        IReadOnlyList<string>? entities = null,
        IReadOnlyList<string>? customApis = null,
        string? serviceContextName = null) =>
        EbgGenerator.BuildConfig(Namespace, serviceContextName, TempOutputPath, entities ?? [], customApis ?? [], SettingsPath, WorkDir);

    // ── Output routing ───────────────────────────────────────────────────────

    [Fact]
    public void BuildConfig_RootPath_IsTheTempOutputFolder()
    {
        // RootPath is what ArgumentBuilder passes to ModelBuilder as the output directory.
        Build().RootPath.Should().Be(TempOutputPath);
    }

    [Fact]
    public void BuildConfig_SettingsAndLog_StayOutOfTheOutputFolder()
    {
        // Anything under TempOutputPath is copied into the user's Models/ by the shared tail.
        var config = Build();

        config.SettingsTemplatePath.Should().Be(SettingsPath);
        config.ExtensionConfig.XrmToolBoxPluginPath.Should().Be(WorkDir);
        config.SettingsTemplatePath.Should().NotStartWith(TempOutputPath);
    }

    [Fact]
    public void BuildConfig_RuntimeDataPaths_AreRootedAtTheToolFolder()
    {
        // EBG roots these against XrmToolBoxPluginPath unless already absolute, and the files ship
        // beside the tool rather than in the DLaB.EarlyBoundGeneratorV2 subfolder EBG assumes.
        var config = Build();

        config.ExtensionConfig.CamelCaseNamesDictionaryRelativePath
            .Should().Be(Path.Combine(AppContext.BaseDirectory, "DLaB.Dictionary.txt"));
        config.ExtensionConfig.TransliterationRelativePath
            .Should().Be(Path.Combine(AppContext.BaseDirectory, "alphabets"));
    }

    [Fact]
    public void DictionaryAndAlphabets_ShipBesideTheTool()
    {
        // CamelCaser throws FileNotFoundException without the dictionary, so a missing copy step
        // breaks every ebg run. NuGet ships only assemblies out of lib/, hence the explicit copy.
        File.Exists(Path.Combine(AppContext.BaseDirectory, "DLaB.Dictionary.txt")).Should().BeTrue();
        Directory.Exists(Path.Combine(AppContext.BaseDirectory, "alphabets")).Should().BeTrue();
    }

    // ── Naming ───────────────────────────────────────────────────────────────

    [Fact]
    public void BuildConfig_Namespace_ComesFromFlowline()
    {
        Build().Namespace.Should().Be(Namespace);
    }

    [Fact]
    public void BuildConfig_ServiceContextName_DefaultsToXrmContext()
    {
        // EBG's own default is DataverseContext; Flowline keeps one name across all generators.
        Build().ServiceContextName.Should().Be("XrmContext");
    }

    [Fact]
    public void BuildConfig_ServiceContextName_HonoursTheOverride()
    {
        Build(serviceContextName: "MyContext").ServiceContextName.Should().Be("MyContext");
    }

    // ── Entity filter ────────────────────────────────────────────────────────

    [Fact]
    public void BuildConfig_EntityFilter_ReplacesEbgDefaultList()
    {
        // EBG defaults to a list of standard tables. Flowline knows the solution contents, so the
        // filter replaces that list rather than adding to it.
        var config = Build(["account", "contact"]);

        config.ExtensionConfig.EntitiesWhitelist.Should().Be("account|contact");
        config.ExtensionConfig.EntityPrefixesWhitelist.Should().BeNull();
    }

    [Fact]
    public void BuildConfig_EntityFilter_UsesPipeSeparator()
    {
        // EBG splits its whitelists on '|', unlike PAC's ';'.
        Build(["account", "contact", "task"]).ExtensionConfig.EntitiesWhitelist.Should().Be("account|contact|task");
    }

    [Fact]
    public void BuildConfig_EmptyEntityFilter_ProducesEmptyWhitelist()
    {
        Build([]).ExtensionConfig.EntitiesWhitelist.Should().BeEmpty();
    }

    // ── Custom API messages ──────────────────────────────────────────────────

    [Fact]
    public void BuildConfig_WithCustomApis_EnablesMessageGeneration()
    {
        var config = Build(["account"], ["av_DoThing", "av_DoOther"]);

        config.GenerateMessages.Should().BeTrue();
        config.ExtensionConfig.ActionsWhitelist.Should().Be("av_DoThing|av_DoOther");
    }

    [Fact]
    public void BuildConfig_WithoutCustomApis_DisablesMessageGeneration()
    {
        // EBG's default whitelist is "analyze", which would generate an unrelated message.
        var config = Build(["account"]);

        config.GenerateMessages.Should().BeFalse();
        config.ExtensionConfig.ActionsWhitelist.Should().BeNull();
        config.ExtensionConfig.ActionPrefixesWhitelist.Should().BeNull();
    }

    // ── Providers ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("DLaB.ModelBuilderExtensions.CustomizeCodeDomService,DLaB.ModelBuilderExtensions")]
    [InlineData("DLaB.ModelBuilderExtensions.CodeGenerationService,DLaB.ModelBuilderExtensions")]
    [InlineData("DLaB.ModelBuilderExtensions.CodeWriterFilterService,DLaB.ModelBuilderExtensions")]
    [InlineData("DLaB.ModelBuilderExtensions.CodeWriterMessageFilterService,DLaB.ModelBuilderExtensions")]
    [InlineData("DLaB.ModelBuilderExtensions.MetadataProviderService,DLaB.ModelBuilderExtensions")]
    [InlineData("DLaB.ModelBuilderExtensions.NamingService,DLaB.ModelBuilderExtensions")]
    public void ProviderTypes_ResolveInProcess(string configuredTypeName)
    {
        // ModelBuilderLib's ServiceFactory resolves providers with Type.GetType and throws
        // NotSupportedException on null. This is the in-process equivalent of the DLL-copying the
        // PAC CLI route needs, and the reason ebg does not touch the PAC installation.
        Type.GetType(configuredTypeName, false).Should().NotBeNull();
    }

    [Fact]
    public void BuildConfig_AudibleCompletionNotification_IsOff()
    {
        // EBG defaults it on; a CLI does not speak.
        Build().AudibleCompletionNotification.Should().BeFalse();
    }
}
