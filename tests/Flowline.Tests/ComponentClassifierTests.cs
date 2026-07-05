using FluentAssertions;
using Flowline.Core.Services;

namespace Flowline.Tests;

public class ComponentClassifierTests : IDisposable
{
    readonly string _dir;

    public ComponentClassifierTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ComponentClassifierTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string SolutionXmlPath => Path.Combine(_dir, "Solution.xml");

    void WriteSolutionXml(string rootComponentsXml) =>
        File.WriteAllText(SolutionXmlPath, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <ImportExportXml>
              <SolutionManifest>
                <UniqueName>MySolution</UniqueName>
                <Version>1.0.0.0</Version>
                <RootComponents>
                  {rootComponentsXml}
                </RootComponents>
              </SolutionManifest>
            </ImportExportXml>
            """);

    // ── Classify — AUTO types ────────────────────────────────────────────────

    [Theory]
    [InlineData(91, ComponentAction.AutoDelete)]  // PluginAssembly (confirmed)
    [InlineData(90, ComponentAction.AutoDelete)]  // PluginType (TODO: verify)
    [InlineData(92, ComponentAction.AutoDelete)]  // SdkMessageProcessingStep (confirmed)
    [InlineData(93, ComponentAction.AutoDelete)]  // SdkMessageProcessingStepImage (TODO: verify)
    [InlineData(61, ComponentAction.AutoDelete)]  // WebResource (confirmed)
    [InlineData(29, ComponentAction.AutoDelete)]  // Workflow (TODO: verify)
    public void Classify_ReturnsAutoDelete_ForAutoComponentTypes(int componentType, ComponentAction expected)
    {
        ComponentClassifier.Classify(componentType).Should().Be(expected);
    }

    // ── Classify — MANUAL types ──────────────────────────────────────────────

    [Theory]
    [InlineData(1)]     // Entity/Table
    [InlineData(2)]     // Attribute/Column
    [InlineData(3)]     // Relationship
    [InlineData(24)]    // Form
    [InlineData(26)]    // View
    [InlineData(20)]    // Role
    [InlineData(10034)] // CustomApi — env-specific, detected via entity query not componenttype
    [InlineData(999)]   // Unknown
    public void Classify_ReturnsManual_ForManualAndUnknownTypes(int componentType)
    {
        ComponentClassifier.Classify(componentType).Should().Be(ComponentAction.Manual);
    }

    [Fact]
    public void Classify_ReturnsManual_ForZero()
    {
        ComponentClassifier.Classify(0).Should().Be(ComponentAction.Manual);
    }

    [Fact]
    public void Classify_ReturnsManual_ForNegativeType()
    {
        ComponentClassifier.Classify(-1).Should().Be(ComponentAction.Manual);
    }

    // ── ParseSolutionXmlComponents — happy paths ─────────────────────────────

    [Fact]
    public void ParseSolutionXmlComponents_ReturnsComponents_ForValidFile()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();
        WriteSolutionXml($$"""
            <RootComponent type="91" id="{{{id1}}}" />
            <RootComponent type="61" id="{{{id2}}}" />
            <RootComponent type="1" id="{{{id3}}}" />
            """);

        var result = ComponentClassifier.ParseSolutionXmlComponents(SolutionXmlPath);

        result.Components.Should().HaveCount(3);
        result.Components.Should().Contain((id1, 91));
        result.Components.Should().Contain((id2, 61));
        result.Components.Should().Contain((id3, 1));
        result.EntityLogicalNames.Should().BeEmpty();
    }

    [Fact]
    public void ParseSolutionXmlComponents_ReturnsEntityLogicalName_ForSchemaNameOnlyEntityRoot()
    {
        WriteSolutionXml("""
            <RootComponent type="1" schemaName="account" behavior="1" />
            <RootComponent type="1" schemaName="contact" behavior="1" />
            """);

        var result = ComponentClassifier.ParseSolutionXmlComponents(SolutionXmlPath);

        result.Components.Should().BeEmpty();
        result.EntityLogicalNames.Should().BeEquivalentTo(["account", "contact"]);
    }

    [Fact]
    public void ParseSolutionXmlComponents_IgnoresSchemaNameOnly_ForNonEntityType()
    {
        WriteSolutionXml("""<RootComponent type="29" schemaName="somename" />""");

        var result = ComponentClassifier.ParseSolutionXmlComponents(SolutionXmlPath);

        result.Components.Should().BeEmpty();
        result.EntityLogicalNames.Should().BeEmpty();
    }

    [Fact]
    public void ParseSolutionXmlComponents_ReturnsEmpty_ForEmptyRootComponents()
    {
        WriteSolutionXml("");

        var result = ComponentClassifier.ParseSolutionXmlComponents(SolutionXmlPath);

        result.Components.Should().BeEmpty();
        result.EntityLogicalNames.Should().BeEmpty();
    }

    [Fact]
    public void ParseSolutionXmlComponents_ReturnsEmpty_WhenRootComponentsMissing()
    {
        File.WriteAllText(SolutionXmlPath, """
            <?xml version="1.0" encoding="utf-8"?>
            <ImportExportXml>
              <SolutionManifest>
                <UniqueName>MySolution</UniqueName>
              </SolutionManifest>
            </ImportExportXml>
            """);

        var result = ComponentClassifier.ParseSolutionXmlComponents(SolutionXmlPath);

        result.Components.Should().BeEmpty();
    }

    [Fact]
    public void ParseSolutionXmlComponents_SkipsComponents_WithMissingOrInvalidAttributes()
    {
        var validId = Guid.NewGuid();
        WriteSolutionXml($$"""
            <RootComponent type="91" id="{{{validId}}}" />
            <RootComponent type="notanint" id="{{{Guid.NewGuid()}}}" />
            <RootComponent type="91" />
            """);

        var result = ComponentClassifier.ParseSolutionXmlComponents(SolutionXmlPath);

        result.Components.Should().HaveCount(1);
        result.Components.Should().Contain((validId, 91));
    }

    // ── ParseSolutionXmlComponents — error paths ─────────────────────────────

    [Fact]
    public void ParseSolutionXmlComponents_Throws_WhenFileIsMissing()
    {
        var missingPath = Path.Combine(_dir, "DoesNotExist.xml");

        var act = () => ComponentClassifier.ParseSolutionXmlComponents(missingPath);

        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void ParseSolutionXmlComponents_Throws_WhenXmlIsMalformed()
    {
        File.WriteAllText(SolutionXmlPath, "this is not xml <<<");

        var act = () => ComponentClassifier.ParseSolutionXmlComponents(SolutionXmlPath);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Solution.xml is malformed*");
    }

    // ── IsWellKnownSystemComponent ────────────────────────────────────────────

    [Theory]
    [InlineData("00000000-0000-0000-00aa-000010001001")] // system view
    [InlineData("00000000-0000-0000-00AA-000010001002")] // case-insensitive
    public void IsWellKnownSystemComponent_ReturnsTrue_ForSystemGuidPrefix(string id)
    {
        ComponentClassifier.IsWellKnownSystemComponent(Guid.Parse(id)).Should().BeTrue();
    }

    [Fact]
    public void IsWellKnownSystemComponent_ReturnsFalse_ForCustomGuid()
    {
        ComponentClassifier.IsWellKnownSystemComponent(Guid.NewGuid()).Should().BeFalse();
    }

    // ── ScanEntitySubcomponents ───────────────────────────────────────────────

    [Fact]
    public void ScanEntitySubcomponents_FindsFormsAndViews_ByGuidFilename()
    {
        var formId = Guid.NewGuid();
        var viewId = Guid.NewGuid();
        var formDir = Path.Combine(_dir, "Entities", "Account", "FormXml", "main");
        var viewDir = Path.Combine(_dir, "Entities", "Account", "SavedQueries");
        Directory.CreateDirectory(formDir);
        Directory.CreateDirectory(viewDir);
        File.WriteAllText(Path.Combine(formDir, $"{{{formId}}}.xml"), "<form/>");
        File.WriteAllText(Path.Combine(viewDir, $"{{{viewId}}}.xml"), "<savedquery/>");

        var result = ComponentClassifier.ScanEntitySubcomponents(_dir);

        result.Should().Contain((formId, 60));
        result.Should().Contain((viewId, 26));
    }

    [Fact]
    public void ScanEntitySubcomponents_IgnoresNonGuidFilenames()
    {
        var viewDir = Path.Combine(_dir, "Entities", "Account", "SavedQueries");
        Directory.CreateDirectory(viewDir);
        File.WriteAllText(Path.Combine(viewDir, "notaguid.xml"), "<savedquery/>");

        var result = ComponentClassifier.ScanEntitySubcomponents(_dir);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ScanEntitySubcomponents_ReturnsEmpty_WhenEntitiesFolderMissing()
    {
        var result = ComponentClassifier.ScanEntitySubcomponents(_dir);

        result.Should().BeEmpty();
    }

    // ── ScanEntityAttributeLogicalNames ───────────────────────────────────────

    [Fact]
    public void ScanEntityAttributeLogicalNames_ReturnsDeclaredAttributes()
    {
        var entityDir = Path.Combine(_dir, "Entities", "Account");
        Directory.CreateDirectory(entityDir);
        File.WriteAllText(Path.Combine(entityDir, "Entity.xml"), """
            <Entity>
              <EntityInfo>
                <entity Name="Account">
                  <attributes>
                    <attribute PhysicalName="av_TaxID"><LogicalName>av_taxid</LogicalName></attribute>
                    <attribute PhysicalName="av_LastActivityOn"><LogicalName>av_lastactivityon</LogicalName></attribute>
                  </attributes>
                </entity>
              </EntityInfo>
            </Entity>
            """);

        var result = ComponentClassifier.ScanEntityAttributeLogicalNames(_dir, "account");

        result.Should().BeEquivalentTo(["av_taxid", "av_lastactivityon"]);
    }

    [Fact]
    public void ScanEntityAttributeLogicalNames_IsCaseInsensitive_ForEntityFolderMatch()
    {
        var entityDir = Path.Combine(_dir, "Entities", "Account");
        Directory.CreateDirectory(entityDir);
        File.WriteAllText(Path.Combine(entityDir, "Entity.xml"), """
            <Entity>
              <EntityInfo>
                <entity Name="Account">
                  <attributes>
                    <attribute PhysicalName="av_TaxID"><LogicalName>av_taxid</LogicalName></attribute>
                  </attributes>
                </entity>
              </EntityInfo>
            </Entity>
            """);

        var result = ComponentClassifier.ScanEntityAttributeLogicalNames(_dir, "ACCOUNT");

        result.Should().Contain("av_taxid");
    }

    [Fact]
    public void ScanEntityAttributeLogicalNames_ReturnsEmpty_WhenEntityFolderMissing()
    {
        var result = ComponentClassifier.ScanEntityAttributeLogicalNames(_dir, "account");

        result.Should().BeEmpty();
    }
}
