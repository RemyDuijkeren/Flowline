using FluentAssertions;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Utils;

namespace Flowline.Tests;

public class PluginWebResourceDriftCheckerTests : IDisposable
{
    readonly string _root;
    readonly string _pkg;

    // Forward slash, not Path.Combine: this constant is used both as a WriteFile relative path (where
    // Path.Combine would accept either separator) and inside a .slnx Path="" attribute written as raw text
    // (WriteSolution), where a literal backslash isn't a separator on Linux/macOS and silently produces a
    // single-segment file name instead of a nested "WebResources/WebResources.csproj" on disk.
    const string WebResourcesProjectRelPath = "WebResources/WebResources.csproj";

    public PluginWebResourceDriftCheckerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"PluginWebResourceDriftCheckerTests_{Guid.NewGuid():N}");
        _pkg = Path.Combine(_root, "Solution");
        Directory.CreateDirectory(_root);

        // A WebResources project is required (R5), so every test needs one on disk and referenced by a
        // solution file (R6) — a plain marker-free csproj resolves as the WebResources project by
        // elimination alone as long as it's the only non-plugin/PCF/test candidate (WebResourcesProjectResolver).
        WriteFile(WebResourcesProjectRelPath,
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        WriteSolution();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ── helpers ──────────────────────────────────────────────────────────────

    void WriteFile(string relativePath, string content = "data")
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    void WriteBinaryFile(string relativePath, int sizeBytes)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[sizeBytes]);
    }

    async Task<List<DriftWarning>> Check(string? publisherPrefix = null)
    {
        var layout = await SolutionFileLayout.LoadAsync(_root);
        return await PluginWebResourceDriftChecker.CheckAsync(_root, layout, _pkg, publisherPrefix);
    }

    /// <summary>Writes a .slnx at the root referencing the WebResources project plus each given plugin project path, as `clone` would.</summary>
    void WriteSolution(params string[] relativePluginProjectPaths)
    {
        var allPaths = new[] { WebResourcesProjectRelPath }.Concat(relativePluginProjectPaths);
        // .slnx stores forward slashes on disk regardless of host OS (see MsBuildSolutionReaderTests'
        // SlnxWithBothProjects) — a literal backslash here would be non-canonical .slnx content.
        var projects = string.Concat(allPaths.Select(p => $"""<Project Path="{p.Replace('\\', '/')}" />"""));
        File.WriteAllText(Path.Combine(_root, "Test.slnx"), $"<Solution>{projects}</Solution>");
    }

    /// <summary>A csproj the plugin pre-filter accepts, so discovery keeps it as a candidate.</summary>
    void WritePluginProject(string relativePath) =>
        WriteFile(relativePath,
            """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net462</TargetFramework></PropertyGroup>
            <ItemGroup><PackageReference Include="Microsoft.CrmSdk.CoreAssemblies" Version="9.0.2" /></ItemGroup></Project>
            """);

    /// <summary>The layout `clone` scaffolds: Plugins/&lt;SolutionName&gt;.Plugins.csproj, listed in the solution file.</summary>
    void ScaffoldPluginProject()
    {
        WritePluginProject(Path.Combine("Plugins", "Test.Plugins.csproj"));
        WriteSolution(@"Plugins\Test.Plugins.csproj");
    }

    // ── no artifacts → nothing to check ─────────────────────────────────────

    [Fact]
    public async Task Check_NeitherDistNorPluginsRelease_ReturnsEmpty()
    {
        (await Check()).Should().BeEmpty();
    }

    [Fact]
    public async Task Check_EmptyDist_ReturnsEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_root, "WebResources", "dist"));

        (await Check()).Should().BeEmpty();
    }

    // ── web resource checks ───────────────────────────────────────────────────

    [Fact]
    public async Task Check_IdenticalFile_NoWarning()
    {
        WriteFile(Path.Combine("WebResources", "dist", "script.js"), "console.log('hi')");
        WriteFile(Path.Combine("Solution", "src", "WebResources", "script.js"), "console.log('hi')");

        (await Check()).Should().BeEmpty();
    }

    [Fact]
    public async Task Check_ContentDiffers_ReturnsContentDiffersWarning()
    {
        WriteFile(Path.Combine("WebResources", "dist", "script.js"), "v1");
        WriteFile(Path.Combine("Solution", "src", "WebResources", "script.js"), "v2");

        (await Check()).Should().ContainSingle(w =>
            w.Category == DriftCategory.ContentDiffers &&
            w.RelativePath.Contains("script.js"));
    }

    [Fact]
    public async Task Check_FileInSrcButNotDist_ReturnsNewInDataverseWarning()
    {
        WriteFile(Path.Combine("WebResources", "dist", "existing.js"), "x");
        WriteFile(Path.Combine("Solution", "src", "WebResources", "existing.js"), "x");
        WriteFile(Path.Combine("Solution", "src", "WebResources", "newfile.js"), "new");

        (await Check()).Should().ContainSingle(w =>
            w.Category == DriftCategory.NewInDataverse &&
            w.RelativePath.Contains("newfile.js"));
    }

    [Fact]
    public async Task Check_FileInDistButNotSrc_ReturnsOnlyLocalWarning()
    {
        WriteFile(Path.Combine("WebResources", "dist", "local.js"), "local");
        // no Solution/src counterpart

        (await Check()).Should().ContainSingle(w =>
            w.Category == DriftCategory.OnlyLocal &&
            w.RelativePath.Contains("local.js"));
    }

    [Fact]
    public async Task Check_MultipleFiles_CorrectCategoryPerFile()
    {
        WriteFile(Path.Combine("WebResources", "dist", "same.js"), "same");
        WriteFile(Path.Combine("WebResources", "dist", "differ.js"), "old");
        WriteFile(Path.Combine("WebResources", "dist", "localonly.js"), "local");

        WriteFile(Path.Combine("Solution", "src", "WebResources", "same.js"), "same");
        WriteFile(Path.Combine("Solution", "src", "WebResources", "differ.js"), "new");
        WriteFile(Path.Combine("Solution", "src", "WebResources", "dataverseonly.js"), "dv");

        var warnings = await Check();

        warnings.Should().Contain(w => w.Category == DriftCategory.ContentDiffers && w.RelativePath.Contains("differ.js"));
        warnings.Should().Contain(w => w.Category == DriftCategory.OnlyLocal && w.RelativePath.Contains("localonly.js"));
        warnings.Should().Contain(w => w.Category == DriftCategory.NewInDataverse && w.RelativePath.Contains("dataverseonly.js"));
        warnings.Should().NotContain(w => w.RelativePath.Contains("same.js"));
    }

    [Fact]
    public async Task Check_DistPopulated_NoSrcWebFolder_AllOnlyLocal()
    {
        WriteFile(Path.Combine("WebResources", "dist", "script.js"), "content");
        // no Solution/src/WebResources folder

        (await Check()).Should().ContainSingle(w =>
            w.Category == DriftCategory.OnlyLocal &&
            w.RelativePath.Contains("script.js"));
    }

    // ── plugin checks ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Check_PluginWithinThreshold_NoWarning()
    {
        var srcSize = 50_000;
        var releaseSize = 50_000 + 5_000; // 5 KB diff — within 10 KB threshold

        ScaffoldPluginProject();
        WriteBinaryFile(Path.Combine("Solution", "src", "PluginAssemblies", "MyPlugin.dll"), srcSize);
        WriteBinaryFile(Path.Combine("Plugins", "bin", "Release", "MyPlugin.dll"), releaseSize);

        (await Check()).Should().BeEmpty();
    }

    [Fact]
    public async Task Check_PluginExceedsThreshold_ReturnsPluginSizeMismatch()
    {
        var srcSize = 50_000;
        var releaseSize = 50_000 + 15_000; // 15 KB diff — exceeds 10 KB threshold

        ScaffoldPluginProject();
        WriteBinaryFile(Path.Combine("Solution", "src", "PluginAssemblies", "MyPlugin.dll"), srcSize);
        WriteBinaryFile(Path.Combine("Plugins", "bin", "Release", "MyPlugin.dll"), releaseSize);

        (await Check()).Should().ContainSingle(w =>
            w.Category == DriftCategory.PluginSizeMismatch &&
            w.RelativePath.Equals("MyPlugin.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Check_ReleaseDllNotInSrcPluginAssemblies_NoWarning()
    {
        ScaffoldPluginProject();
        WriteBinaryFile(Path.Combine("Plugins", "bin", "Release", "Unrelated.dll"), 100_000);
        // no matching name in Solution/src/PluginAssemblies

        (await Check()).Should().BeEmpty();
    }

    [Fact]
    public async Task Check_SrcPluginAssembliesExists_NoReleaseFolder_NoWarning()
    {
        ScaffoldPluginProject();
        WriteBinaryFile(Path.Combine("Solution", "src", "PluginAssemblies", "MyPlugin.dll"), 50_000);
        // no Plugins/bin/Release folder

        (await Check()).Should().BeEmpty();
    }

    // ── U5: plugin output located through the solution file ───────────────────
    // Before discovery these checks read a literal Plugins/bin/Release. A project under any other name
    // went unchecked with no warning at all, which is the silent half of the failure.

    [Fact]
    public async Task Check_PluginProjectNotNamedPlugins_StillDriftChecked()
    {
        WritePluginProject(Path.Combine("Sales", "AV.Sales.Plugins.csproj"));
        WriteSolution(@"Sales\AV.Sales.Plugins.csproj");

        WriteBinaryFile(Path.Combine("Solution", "src", "PluginAssemblies", "AV.Sales.Plugins.dll"), 50_000);
        WriteBinaryFile(Path.Combine("Sales", "bin", "Release", "AV.Sales.Plugins.dll"), 50_000 + 15_000);

        (await Check()).Should().ContainSingle(w =>
            w.Category == DriftCategory.PluginSizeMismatch &&
            w.RelativePath.Equals("AV.Sales.Plugins.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Check_TwoPluginProjects_BothAssembliesDriftChecked()
    {
        WritePluginProject(Path.Combine("Sales", "Sales.Plugins.csproj"));
        WritePluginProject(Path.Combine("Support", "Support.Plugins.csproj"));
        WriteSolution(@"Sales\Sales.Plugins.csproj", @"Support\Support.Plugins.csproj");

        WriteBinaryFile(Path.Combine("Solution", "src", "PluginAssemblies", "Sales.Plugins.dll"), 50_000);
        WriteBinaryFile(Path.Combine("Sales", "bin", "Release", "Sales.Plugins.dll"), 50_000 + 15_000);
        WriteBinaryFile(Path.Combine("Solution", "src", "PluginAssemblies", "Support.Plugins.dll"), 50_000);
        WriteBinaryFile(Path.Combine("Support", "bin", "Release", "Support.Plugins.dll"), 50_000 + 15_000);

        var warnings = await Check();

        // The second project is the assertion that matters: the old code stopped after one folder.
        warnings.Should().HaveCount(2);
        warnings.Should().Contain(w => w.Category == DriftCategory.PluginSizeMismatch && w.RelativePath == "Sales.Plugins.dll");
        warnings.Should().Contain(w => w.Category == DriftCategory.PluginSizeMismatch && w.RelativePath == "Support.Plugins.dll");
    }

    [Fact]
    public async Task Check_TwoPluginProjects_AssemblyBuiltByEitherIsNotOrphan()
    {
        WritePluginProject(Path.Combine("Sales", "Sales.Plugins.csproj"));
        WritePluginProject(Path.Combine("Support", "Support.Plugins.csproj"));
        WriteSolution(@"Sales\Sales.Plugins.csproj", @"Support\Support.Plugins.csproj");

        WriteBinaryFile(Path.Combine("Sales", "bin", "Release", "Sales.Plugins.dll"), 1_000);
        WriteBinaryFile(Path.Combine("Support", "bin", "Release", "Support.Plugins.dll"), 1_000);

        WriteBinaryFile(Path.Combine("Solution", "src", "PluginAssemblies", "Sales.Plugins.dll"), 1_000);
        WriteBinaryFile(Path.Combine("Solution", "src", "PluginAssemblies", "Support.Plugins.dll"), 1_000);
        WriteBinaryFile(Path.Combine("Solution", "src", "PluginAssemblies", "Retired.Plugins.dll"), 1_000);

        (await Check()).Should().ContainSingle(w =>
            w.Category == DriftCategory.OrphanAssembly && w.RelativePath == "Retired.Plugins.dll");
    }

    [Fact]
    public async Task Check_StalePluginsDllAfterProjectRename_ReportedAsOrphan()
    {
        WritePluginProject(Path.Combine("Sales", "AV.Sales.Plugins.csproj"));
        WriteSolution(@"Sales\AV.Sales.Plugins.csproj");

        WriteBinaryFile(Path.Combine("Sales", "bin", "Release", "AV.Sales.Plugins.dll"), 1_000);
        WriteBinaryFile(Path.Combine("Solution", "src", "PluginAssemblies", "AV.Sales.Plugins.dll"), 1_000);
        // Left behind by the rename — no project builds it any more.
        WriteBinaryFile(Path.Combine("Solution", "src", "PluginAssemblies", "Plugins.dll"), 1_000);

        (await Check()).Should().ContainSingle(w =>
            w.Category == DriftCategory.OrphanAssembly && w.RelativePath == "Plugins.dll");
    }

    // ── classic dotted assembly: PAC strips the dot from the on-disk name, .data.xml keeps the truth ─
    // PAC unpacks a classic PluginAssembly named "Cr07982.LegacyPlugins" to a dot-stripped file
    // "Cr07982LegacyPlugins.dll". Build output keeps the dot. Matching on the raw file name misreports
    // a live, correctly-registered assembly as an orphan (and hides genuine size drift) — the companion
    // .data.xml's FullName carries the true identity, so the checker compares on that.

    /// <summary>Writes the companion &lt;dll&gt;.data.xml PAC unpacks next to a classic PluginAssembly.</summary>
    void WriteAssemblyMetadata(string relativeDllPath, string fullName, bool asElement = false) =>
        WriteFile(relativeDllPath + ".data.xml", asElement
            ? $"""<?xml version="1.0" encoding="utf-8"?><PluginAssembly><FullName>{fullName}</FullName></PluginAssembly>"""
            : $"""<?xml version="1.0" encoding="utf-8"?><PluginAssembly FullName="{fullName}" />""");

    const string DottedFullName = "Cr07982.LegacyPlugins, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";

    [Fact]
    public async Task Check_ClassicDottedAssembly_PacStrippedFilename_NotReportedAsOrphan()
    {
        WritePluginProject(Path.Combine("Legacy", "Cr07982.LegacyPlugins.csproj"));
        WriteSolution(@"Legacy\Cr07982.LegacyPlugins.csproj");
        WriteBinaryFile(Path.Combine("Legacy", "bin", "Release", "Cr07982.LegacyPlugins.dll"), 1_000);

        // Unpacked with the dot stripped, but the metadata keeps the true name.
        var unpacked = Path.Combine("Solution", "src", "PluginAssemblies", "Cr07982LegacyPlugins.dll");
        WriteBinaryFile(unpacked, 1_000);
        WriteAssemblyMetadata(unpacked, DottedFullName);

        (await Check()).Should().BeEmpty();
    }

    [Fact]
    public async Task Check_ClassicDottedAssembly_FullNameAsElement_NotReportedAsOrphan()
    {
        WritePluginProject(Path.Combine("Legacy", "Cr07982.LegacyPlugins.csproj"));
        WriteSolution(@"Legacy\Cr07982.LegacyPlugins.csproj");
        WriteBinaryFile(Path.Combine("Legacy", "bin", "Release", "Cr07982.LegacyPlugins.dll"), 1_000);

        var unpacked = Path.Combine("Solution", "src", "PluginAssemblies", "Cr07982LegacyPlugins.dll");
        WriteBinaryFile(unpacked, 1_000);
        WriteAssemblyMetadata(unpacked, DottedFullName, asElement: true);

        (await Check()).Should().BeEmpty();
    }

    [Fact]
    public async Task Check_ClassicDottedAssembly_SizeMismatchReportedUnderTrueName()
    {
        WritePluginProject(Path.Combine("Legacy", "Cr07982.LegacyPlugins.csproj"));
        WriteSolution(@"Legacy\Cr07982.LegacyPlugins.csproj");
        WriteBinaryFile(Path.Combine("Legacy", "bin", "Release", "Cr07982.LegacyPlugins.dll"), 50_000 + 15_000);

        var unpacked = Path.Combine("Solution", "src", "PluginAssemblies", "Cr07982LegacyPlugins.dll");
        WriteBinaryFile(unpacked, 50_000);
        WriteAssemblyMetadata(unpacked, DottedFullName);

        (await Check()).Should().ContainSingle(w =>
            w.Category == DriftCategory.PluginSizeMismatch &&
            w.RelativePath.Equals("Cr07982.LegacyPlugins.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Check_ClassicDottedAssembly_NoBuildingProject_ReportedAsOrphanUnderTrueName()
    {
        // A genuinely orphaned dotted classic assembly is still flagged — under its true dotted
        // identity, not PAC's dot-stripped file name.
        WritePluginProject(Path.Combine("Sales", "Sales.Plugins.csproj"));
        WriteSolution(@"Sales\Sales.Plugins.csproj");
        WriteBinaryFile(Path.Combine("Sales", "bin", "Release", "Sales.Plugins.dll"), 1_000);
        WriteBinaryFile(Path.Combine("Solution", "src", "PluginAssemblies", "Sales.Plugins.dll"), 1_000);

        var orphan = Path.Combine("Solution", "src", "PluginAssemblies", "Cr07982LegacyPlugins.dll");
        WriteBinaryFile(orphan, 1_000);
        WriteAssemblyMetadata(orphan, DottedFullName);

        (await Check()).Should().ContainSingle(w =>
            w.Category == DriftCategory.OrphanAssembly && w.RelativePath == "Cr07982.LegacyPlugins.dll");
    }

    [Fact]
    public async Task Check_UnpackedAssembly_MalformedDataXml_FallsBackToFileName()
    {
        // A .data.xml that won't parse must not throw — the checker falls back to the on-disk file name.
        WritePluginProject(Path.Combine("Sales", "Sales.Plugins.csproj"));
        WriteSolution(@"Sales\Sales.Plugins.csproj");
        WriteBinaryFile(Path.Combine("Sales", "bin", "Release", "Sales.Plugins.dll"), 1_000);

        var unpacked = Path.Combine("Solution", "src", "PluginAssemblies", "Sales.Plugins.dll");
        WriteBinaryFile(unpacked, 1_000);
        WriteFile(unpacked + ".data.xml", "<PluginAssembly> not valid xml");

        (await Check()).Should().BeEmpty();
    }

    // ── null WebResources project: skip web-resource drift, still check plugins, don't throw ─────

    [Fact]
    public async Task Check_NoWebResourcesProject_SkipsWebResourceDrift_StillChecksPlugins()
    {
        // A plugin-only solution: no WebResources project referenced, so WebResourcesProjectPath resolves to
        // null. CheckAsync must skip the web-resource half (even with a populated dist/ on disk) and still
        // report plugin drift — never throw. The single loud warning is the command caller's job, not here.
        WritePluginProject(Path.Combine("Plugins", "Test.Plugins.csproj"));
        File.WriteAllText(Path.Combine(_root, "Test.slnx"),
            """<Solution><Project Path="Plugins/Test.Plugins.csproj" /></Solution>""");

        // A dist/ that would drift if it were checked — proves the web-resource half is genuinely skipped.
        WriteFile(Path.Combine("WebResources", "dist", "local.js"), "local");
        WriteBinaryFile(Path.Combine("Solution", "src", "PluginAssemblies", "Test.Plugins.dll"), 50_000);
        WriteBinaryFile(Path.Combine("Plugins", "bin", "Release", "Test.Plugins.dll"), 50_000 + 15_000);

        var layout = await SolutionFileLayout.LoadAsync(_root);
        layout.WebResourcesProjectPath.Should().BeNull();

        var warnings = await PluginWebResourceDriftChecker.CheckAsync(_root, layout, _pkg);

        warnings.Should().ContainSingle(w =>
            w.Category == DriftCategory.PluginSizeMismatch && w.RelativePath == "Test.Plugins.dll");
        warnings.Should().NotContain(w => w.RelativePath.Contains("local.js"));
    }

    [Fact]
    public async Task Check_NoSolutionFile_ThrowsRatherThanFallingBackToConventionalPluginsFolder()
    {
        // R6: no solution file is an error now, not a fallback to the conventional Plugins/ folder.
        File.Delete(Path.Combine(_root, "Test.slnx"));
        WriteBinaryFile(Path.Combine("Solution", "src", "PluginAssemblies", "MyPlugin.dll"), 50_000);
        WriteBinaryFile(Path.Combine("Plugins", "bin", "Release", "MyPlugin.dll"), 50_000 + 15_000);

        var act = async () => await Check();

        (await act.Should().ThrowAsync<FlowlineException>()).Which.ExitCode.Should().Be(ExitCode.NotFound);
    }
}
