using FluentAssertions;
using Flowline.Utils;

namespace Flowline.Tests;

public class DriftCheckerTests : IDisposable
{
    readonly string _root;
    readonly string _pkg;

    public DriftCheckerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"DriftCheckerTests_{Guid.NewGuid():N}");
        _pkg = Path.Combine(_root, "Package");
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

    List<DriftWarning> Check(string? publisherPrefix = null) =>
        DriftChecker.Check(_root, _pkg, publisherPrefix);

    // ── no artifacts → nothing to check ─────────────────────────────────────

    [Fact]
    public void Check_NeitherDistNorPluginsRelease_ReturnsEmpty()
    {
        Check().Should().BeEmpty();
    }

    [Fact]
    public void Check_EmptyDist_ReturnsEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_root, "WebResources", "dist"));

        Check().Should().BeEmpty();
    }

    // ── web resource checks ───────────────────────────────────────────────────

    [Fact]
    public void Check_IdenticalFile_NoWarning()
    {
        WriteFile(Path.Combine("WebResources", "dist", "script.js"), "console.log('hi')");
        WriteFile(Path.Combine("Package", "src", "WebResources", "script.js"), "console.log('hi')");

        Check().Should().BeEmpty();
    }

    [Fact]
    public void Check_ContentDiffers_ReturnsContentDiffersWarning()
    {
        WriteFile(Path.Combine("WebResources", "dist", "script.js"), "v1");
        WriteFile(Path.Combine("Package", "src", "WebResources", "script.js"), "v2");

        Check().Should().ContainSingle(w =>
            w.Category == DriftCategory.ContentDiffers &&
            w.RelativePath.Contains("script.js"));
    }

    [Fact]
    public void Check_FileInSrcButNotDist_ReturnsNewInDataverseWarning()
    {
        WriteFile(Path.Combine("WebResources", "dist", "existing.js"), "x");
        WriteFile(Path.Combine("Package", "src", "WebResources", "existing.js"), "x");
        WriteFile(Path.Combine("Package", "src", "WebResources", "newfile.js"), "new");

        Check().Should().ContainSingle(w =>
            w.Category == DriftCategory.NewInDataverse &&
            w.RelativePath.Contains("newfile.js"));
    }

    [Fact]
    public void Check_FileInDistButNotSrc_ReturnsOnlyLocalWarning()
    {
        WriteFile(Path.Combine("WebResources", "dist", "local.js"), "local");
        // no Package/src counterpart

        Check().Should().ContainSingle(w =>
            w.Category == DriftCategory.OnlyLocal &&
            w.RelativePath.Contains("local.js"));
    }

    [Fact]
    public void Check_MultipleFiles_CorrectCategoryPerFile()
    {
        WriteFile(Path.Combine("WebResources", "dist", "same.js"), "same");
        WriteFile(Path.Combine("WebResources", "dist", "differ.js"), "old");
        WriteFile(Path.Combine("WebResources", "dist", "localonly.js"), "local");

        WriteFile(Path.Combine("Package", "src", "WebResources", "same.js"), "same");
        WriteFile(Path.Combine("Package", "src", "WebResources", "differ.js"), "new");
        WriteFile(Path.Combine("Package", "src", "WebResources", "dataverseonly.js"), "dv");

        var warnings = Check();

        warnings.Should().Contain(w => w.Category == DriftCategory.ContentDiffers && w.RelativePath.Contains("differ.js"));
        warnings.Should().Contain(w => w.Category == DriftCategory.OnlyLocal && w.RelativePath.Contains("localonly.js"));
        warnings.Should().Contain(w => w.Category == DriftCategory.NewInDataverse && w.RelativePath.Contains("dataverseonly.js"));
        warnings.Should().NotContain(w => w.RelativePath.Contains("same.js"));
    }

    [Fact]
    public void Check_DistPopulated_NoSrcWebFolder_AllOnlyLocal()
    {
        WriteFile(Path.Combine("WebResources", "dist", "script.js"), "content");
        // no Package/src/WebResources folder

        Check().Should().ContainSingle(w =>
            w.Category == DriftCategory.OnlyLocal &&
            w.RelativePath.Contains("script.js"));
    }

    // ── plugin checks ─────────────────────────────────────────────────────────

    [Fact]
    public void Check_PluginWithinThreshold_NoWarning()
    {
        var srcSize = 50_000;
        var releaseSize = 50_000 + 5_000; // 5 KB diff — within 10 KB threshold

        WriteBinaryFile(Path.Combine("Package", "src", "PluginAssemblies", "MyPlugin.dll"), srcSize);
        WriteBinaryFile(Path.Combine("Plugins", "bin", "Release", "MyPlugin.dll"), releaseSize);

        Check().Should().BeEmpty();
    }

    [Fact]
    public void Check_PluginExceedsThreshold_ReturnsPluginSizeMismatch()
    {
        var srcSize = 50_000;
        var releaseSize = 50_000 + 15_000; // 15 KB diff — exceeds 10 KB threshold

        WriteBinaryFile(Path.Combine("Package", "src", "PluginAssemblies", "MyPlugin.dll"), srcSize);
        WriteBinaryFile(Path.Combine("Plugins", "bin", "Release", "MyPlugin.dll"), releaseSize);

        Check().Should().ContainSingle(w =>
            w.Category == DriftCategory.PluginSizeMismatch &&
            w.RelativePath.Equals("MyPlugin.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Check_ReleaseDllNotInSrcPluginAssemblies_NoWarning()
    {
        WriteBinaryFile(Path.Combine("Plugins", "bin", "Release", "Unrelated.dll"), 100_000);
        // no matching name in Package/src/PluginAssemblies

        Check().Should().BeEmpty();
    }

    [Fact]
    public void Check_SrcPluginAssembliesExists_NoReleaseFolder_NoWarning()
    {
        WriteBinaryFile(Path.Combine("Package", "src", "PluginAssemblies", "MyPlugin.dll"), 50_000);
        // no Plugins/bin/Release folder

        Check().Should().BeEmpty();
    }
}
