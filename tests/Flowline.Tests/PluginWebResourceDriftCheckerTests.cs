using FluentAssertions;
using Flowline.Utils;

namespace Flowline.Tests;

public class PluginWebResourceDriftCheckerTests : IDisposable
{
    readonly string _root;
    readonly string _pkg;

    public PluginWebResourceDriftCheckerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"PluginWebResourceDriftCheckerTests_{Guid.NewGuid():N}");
        _pkg = Path.Combine(_root, "Solution");
        Directory.CreateDirectory(_root);
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

    Task<List<DriftWarning>> Check(string? publisherPrefix = null) =>
        PluginWebResourceDriftChecker.CheckAsync(_root, _pkg, publisherPrefix);

    /// <summary>Writes a .slnx at the root referencing each given project path, as `clone` would.</summary>
    void WriteSolution(params string[] relativeProjectPaths)
    {
        var projects = string.Concat(relativeProjectPaths.Select(p => $"""<Project Path="{p}" />"""));
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

    [Fact]
    public async Task Check_NoSolutionFile_FallsBackToConventionalPluginsFolder()
    {
        // A partially-set-up repo: no solution file, but a built conventional Plugins project.
        WriteBinaryFile(Path.Combine("Solution", "src", "PluginAssemblies", "MyPlugin.dll"), 50_000);
        WriteBinaryFile(Path.Combine("Plugins", "bin", "Release", "MyPlugin.dll"), 50_000 + 15_000);

        var act = async () => await Check();

        await act.Should().NotThrowAsync();
        (await Check()).Should().ContainSingle(w => w.Category == DriftCategory.PluginSizeMismatch);
    }
}
