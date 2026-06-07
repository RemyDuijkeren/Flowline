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
    [InlineData(1)]   // Entity/Table
    [InlineData(2)]   // Attribute/Column
    [InlineData(3)]   // Relationship
    [InlineData(24)]  // Form
    [InlineData(26)]  // View
    [InlineData(20)]  // Role
    [InlineData(999)] // Unknown
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

        result.Should().HaveCount(3);
        result.Should().Contain((id1, 91));
        result.Should().Contain((id2, 61));
        result.Should().Contain((id3, 1));
    }

    [Fact]
    public void ParseSolutionXmlComponents_ReturnsEmpty_ForEmptyRootComponents()
    {
        WriteSolutionXml("");

        var result = ComponentClassifier.ParseSolutionXmlComponents(SolutionXmlPath);

        result.Should().BeEmpty();
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

        result.Should().BeEmpty();
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

        result.Should().HaveCount(1);
        result.Should().Contain((validId, 91));
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
}
