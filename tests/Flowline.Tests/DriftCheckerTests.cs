using FluentAssertions;
using Flowline.Utils;

namespace Flowline.Tests;

public class DriftCheckerTests : IDisposable
{
    readonly string _root;

    public DriftCheckerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"DriftCheckerTests_{Guid.NewGuid():N}");
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

    // ── no artifacts → nothing to check ─────────────────────────────────────

    [Fact]
    public async Task CheckAsync_NeitherDistNorPluginsRelease_ReturnsEmpty()
    {
        var warnings = await DriftChecker.CheckAsync(_root);

        warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckAsync_EmptyDist_ReturnsEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_root, "WebResources", "dist"));

        var warnings = await DriftChecker.CheckAsync(_root);

        warnings.Should().BeEmpty();
    }

    // ── web resource checks ───────────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_IdenticalFile_NoWarning()
    {
        WriteFile(@"WebResources\dist\script.js", "console.log('hi')");
        WriteFile(@"src\WebResources\script.js", "console.log('hi')");

        var warnings = await DriftChecker.CheckAsync(_root);

        warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckAsync_ContentDiffers_ReturnsContentDiffersWarning()
    {
        WriteFile(@"WebResources\dist\script.js", "v1");
        WriteFile(@"src\WebResources\script.js", "v2");

        var warnings = await DriftChecker.CheckAsync(_root);

        warnings.Should().ContainSingle(w =>
            w.Category == DriftCategory.ContentDiffers &&
            w.RelativePath.Contains("script.js"));
    }

    [Fact]
    public async Task CheckAsync_FileInSrcButNotDist_ReturnsNewInDataverseWarning()
    {
        WriteFile(@"WebResources\dist\existing.js", "x");
        WriteFile(@"src\WebResources\existing.js", "x");
        WriteFile(@"src\WebResources\newfile.js", "new");

        var warnings = await DriftChecker.CheckAsync(_root);

        warnings.Should().ContainSingle(w =>
            w.Category == DriftCategory.NewInDataverse &&
            w.RelativePath.Contains("newfile.js"));
    }

    [Fact]
    public async Task CheckAsync_FileInDistButNotSrc_ReturnsOnlyLocalWarning()
    {
        WriteFile(@"WebResources\dist\local.js", "local");
        // no src counterpart

        var warnings = await DriftChecker.CheckAsync(_root);

        warnings.Should().ContainSingle(w =>
            w.Category == DriftCategory.OnlyLocal &&
            w.RelativePath.Contains("local.js"));
    }

    [Fact]
    public async Task CheckAsync_MultipleFiles_CorrectCategoryPerFile()
    {
        WriteFile(@"WebResources\dist\same.js", "same");
        WriteFile(@"WebResources\dist\differ.js", "old");
        WriteFile(@"WebResources\dist\localonly.js", "local");

        WriteFile(@"src\WebResources\same.js", "same");
        WriteFile(@"src\WebResources\differ.js", "new");
        WriteFile(@"src\WebResources\dataverseonly.js", "dv");

        var warnings = await DriftChecker.CheckAsync(_root);

        warnings.Should().Contain(w => w.Category == DriftCategory.ContentDiffers && w.RelativePath.Contains("differ.js"));
        warnings.Should().Contain(w => w.Category == DriftCategory.OnlyLocal && w.RelativePath.Contains("localonly.js"));
        warnings.Should().Contain(w => w.Category == DriftCategory.NewInDataverse && w.RelativePath.Contains("dataverseonly.js"));
        warnings.Should().NotContain(w => w.RelativePath.Contains("same.js"));
    }

    // ── plugin checks ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_PluginWithinThreshold_NoWarning()
    {
        var srcSize = 50_000;
        var releaseSize = 50_000 + 5_000; // 5 KB diff — within 10 KB threshold

        WriteBinaryFile(@"src\PluginAssemblies\MyPlugin.dll", srcSize);
        WriteBinaryFile(@"Plugins\bin\Release\MyPlugin.dll", releaseSize);

        var warnings = await DriftChecker.CheckAsync(_root);

        warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckAsync_PluginExceedsThreshold_ReturnsPluginSizeMismatch()
    {
        var srcSize = 50_000;
        var releaseSize = 50_000 + 15_000; // 15 KB diff — exceeds 10 KB threshold

        WriteBinaryFile(@"src\PluginAssemblies\MyPlugin.dll", srcSize);
        WriteBinaryFile(@"Plugins\bin\Release\MyPlugin.dll", releaseSize);

        var warnings = await DriftChecker.CheckAsync(_root);

        warnings.Should().ContainSingle(w =>
            w.Category == DriftCategory.PluginSizeMismatch &&
            w.RelativePath.Equals("MyPlugin.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CheckAsync_ReleaseDllNotInSrcPluginAssemblies_NoWarning()
    {
        WriteBinaryFile(@"Plugins\bin\Release\Unrelated.dll", 100_000);
        // no matching name in src/PluginAssemblies

        var warnings = await DriftChecker.CheckAsync(_root);

        warnings.Should().BeEmpty();
    }
}
