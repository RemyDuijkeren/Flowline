using FluentAssertions;
using Flowline.Core.Deploy;

namespace Flowline.Core.Tests.Deploy;

public class BuildOutputSlotMatcherTests : IDisposable
{
    readonly string _src;

    public BuildOutputSlotMatcherTests()
    {
        _src = Path.Combine(Path.GetTempPath(), $"BuildOutputSlotMatcherTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_src);
    }

    public void Dispose() => Directory.Delete(_src, recursive: true);

    void Write(string relativePath, string content = "x")
    {
        var full = Path.Combine(_src, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    void WriteAssemblySlot(string folder, string dllFile, string fullName) =>
        Write($"PluginAssemblies/{folder}/{dllFile}.data.xml",
            $"""<?xml version="1.0"?><PluginAssembly FullName="{fullName}, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null" />""");

    void WriteWebResourceSlot(string file, string name) =>
        Write($"WebResources/{file}.data.xml", $"<WebResource><Name>{name}</Name></WebResource>");

    static readonly IReadOnlyList<WebResourceBuildOutput> NoWeb = [];
    static readonly IReadOnlyList<PluginBuildOutput> NoPlugins = [];

    [Fact]
    public void Match_ClassicDll_MatchesSlotByFullNameNotFolderName()
    {
        var folder = "AVSpotlerAutomatePlugins-CA6C957A-1111-2222-3333-444444444444";
        WriteAssemblySlot(folder, "AVSpotlerAutomatePlugins.dll", "AV.SpotlerAutomate.Plugins");

        var slots = BuildOutputSlotMatcher.FindSlots(_src);
        var result = BuildOutputSlotMatcher.Match(slots,
            [new PluginBuildOutput("AV.SpotlerAutomate.Plugins", "/b/AV.SpotlerAutomate.Plugins.dll", false)],
            NoWeb, "Sales");

        slots.Should().ContainSingle().Which.Key.Should().Be("AV.SpotlerAutomate.Plugins");
        result.Copies.Should().ContainSingle().Which.Should().Be(new SlotCopy(
            "/b/AV.SpotlerAutomate.Plugins.dll", $"PluginAssemblies/{folder}/AVSpotlerAutomatePlugins.dll"));
        result.UnmatchedOutputs.Should().BeEmpty();
        result.SlotsWithoutOutput.Should().BeEmpty();
    }

    [Fact]
    public void FindSlots_MetadataOnly_StillReturnsSlot()
    {
        WriteAssemblySlot("Foo-1", "Foo.dll", "Foo");
        File.Exists(Path.Combine(_src, "PluginAssemblies/Foo-1/Foo.dll")).Should().BeFalse();

        BuildOutputSlotMatcher.FindSlots(_src).Should().ContainSingle(s => s.Kind == SlotKind.PluginAssembly);
    }

    [Fact]
    public void FindSlots_ServiceEndpointsXml_IsNotASlot()
    {
        Write("PluginAssemblies/ServiceEndpoints.xml", "<ServiceEndpoints />");

        BuildOutputSlotMatcher.FindSlots(_src).Should().BeEmpty();
    }

    [Fact]
    public void FindSlots_MissingFolders_ReturnsEmpty() =>
        BuildOutputSlotMatcher.FindSlots(_src).Should().BeEmpty();

    [Fact]
    public void Match_Nupkg_MatchesPackageSlotAndTargetsExistingNupkgName()
    {
        Write("pluginpackages/cr_Contoso.Plugins/pluginpackage.xml", """<pluginpackage uniquename="cr_Contoso.Plugins" />""");
        Write("pluginpackages/cr_Contoso.Plugins/package/cr_Contoso.Plugins.nupkg");

        var slots = BuildOutputSlotMatcher.FindSlots(_src);
        var result = BuildOutputSlotMatcher.Match(slots,
            [new PluginBuildOutput("Contoso.Plugins", "/b/Contoso.Plugins.1.0.nupkg", true)], NoWeb, "Sales");

        result.Copies.Should().ContainSingle().Which.SlotRelativePath
            .Should().Be("pluginpackages/cr_Contoso.Plugins/package/cr_Contoso.Plugins.nupkg");
        result.UnmatchedOutputs.Should().BeEmpty();
    }

    [Fact]
    public void FindSlots_PackageWithoutNupkg_UsesUniqueNameAsFileName()
    {
        Write("pluginpackages/cr_Contoso.Plugins/pluginpackage.xml", """<pluginpackage uniquename="cr_Contoso.Plugins" />""");

        BuildOutputSlotMatcher.FindSlots(_src).Should().ContainSingle().Which.BinaryRelativePath
            .Should().Be("pluginpackages/cr_Contoso.Plugins/package/cr_Contoso.Plugins.nupkg");
    }

    [Fact]
    public void Match_DistFile_MatchesPrefixedSlotCaseInsensitively()
    {
        WriteWebResourceSlot("cr_Sales/main.js", "cr_Sales/main.js");

        var slots = BuildOutputSlotMatcher.FindSlots(_src);
        var result = BuildOutputSlotMatcher.Match(slots, NoPlugins,
            [new WebResourceBuildOutput("MAIN.js", "/d/MAIN.js")], "sales");

        result.Copies.Should().ContainSingle().Which.SlotRelativePath.Should().Be("WebResources/cr_Sales/main.js");
        result.UnmatchedOutputs.Should().BeEmpty();
    }

    [Fact]
    public void Match_DistFileWithPrefixInFirstSegment_MatchesVerbatim()
    {
        WriteWebResourceSlot("cr_logo.svg", "cr_logo.svg");

        var result = BuildOutputSlotMatcher.Match(BuildOutputSlotMatcher.FindSlots(_src), NoPlugins,
            [new WebResourceBuildOutput("cr_logo.svg", "/d/cr_logo.svg")], "Sales");

        result.Copies.Should().ContainSingle();
        result.UnmatchedOutputs.Should().BeEmpty();
    }

    [Fact]
    public void Match_AssemblyWithoutSlot_IsUnmatched()
    {
        var result = BuildOutputSlotMatcher.Match([],
            [new PluginBuildOutput("Orphan.Plugins", "/b/Orphan.Plugins.dll", false)], NoWeb, "Sales");

        result.UnmatchedOutputs.Should().ContainSingle().Which.Should().Be("Orphan.Plugins");
        result.Copies.Should().BeEmpty();
    }

    [Fact]
    public void Match_WebResourceSlotWithoutDistFile_IsSlotWithoutOutput()
    {
        WriteWebResourceSlot("cr_Sales/main.js", "cr_Sales/main.js");

        var result = BuildOutputSlotMatcher.Match(BuildOutputSlotMatcher.FindSlots(_src), NoPlugins, NoWeb, "Sales");

        result.SlotsWithoutOutput.Should().ContainSingle().Which.Key.Should().Be("cr_Sales/main.js");
    }
}

public class BuildOutputSlotMatcherDuplicateTests
{
    [Fact]
    public void Match_ReportsBothOutputs_WhenTwoMapToTheSameSlot()
    {
        var slot = new SolutionSlot(SlotKind.WebResource, "cr_Sales/main.js", "WebResources/cr_Sales/main.js");

        var result = BuildOutputSlotMatcher.Match([slot], [],
            [new WebResourceBuildOutput("main.js", "/dist/main.js"), new WebResourceBuildOutput("MAIN.js", "/dist/MAIN.js")], "Sales");

        result.DuplicateOutputs.Should().ContainSingle()
            .Which.Should().Contain("main.js").And.Contain("MAIN.js");
        result.Copies.Should().ContainSingle();
    }

    [Fact]
    public void Match_ReportsBothOutputs_WhenTwoPluginProjectsBuildTheSameAssembly()
    {
        var slot = new SolutionSlot(SlotKind.PluginAssembly, "Contoso.Plugins", "PluginAssemblies/ContosoPlugins-1/ContosoPlugins.dll");

        var result = BuildOutputSlotMatcher.Match([slot],
            [new PluginBuildOutput("Contoso.Plugins", "/a/Contoso.Plugins.dll", false), new PluginBuildOutput("Contoso.Plugins", "/b/Contoso.Plugins.dll", false)], [], "Sales");

        result.DuplicateOutputs.Should().ContainSingle();
    }
}
