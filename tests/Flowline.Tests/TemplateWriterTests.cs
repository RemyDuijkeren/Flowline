using FluentAssertions;
using Flowline.Core;
using Flowline.Utils;
using Xunit;

namespace Flowline.Tests;

public class TemplateWriterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Theory]
    [InlineData("Flowline.Templates.WebResources.WebResources.csproj")]
    [InlineData("Flowline.Templates.WebResources.package.json")]
    [InlineData("Flowline.Templates.WebResources.rollup.config.mjs")]
    [InlineData("Flowline.Templates.WebResources.tsconfig.json")]
    [InlineData("Flowline.Templates.WebResources.eslint.config.mjs")]
    [InlineData("Flowline.Templates.WebResources.README.md")]
    [InlineData("Flowline.Templates.WebResources.src.example.ts")]
    public async Task WriteAsync_WritesFile_WhenResourceExists(string logicalName)
    {
        var targetPath = Path.Combine(_tempDir, Path.GetFileName(logicalName));

        await TemplateWriter.WriteAsync(logicalName, targetPath);

        File.Exists(targetPath).Should().BeTrue();
        new FileInfo(targetPath).Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task WriteAsync_CreatesParentDirectory_WhenMissing()
    {
        var nested = Path.Combine(_tempDir, "a", "b", "c", "package.json");

        await TemplateWriter.WriteAsync("Flowline.Templates.WebResources.package.json", nested);

        File.Exists(nested).Should().BeTrue();
    }

    [Fact]
    public async Task WriteAsync_ThrowsFlowlineException_WhenResourceNotFound()
    {
        var act = () => TemplateWriter.WriteAsync("Flowline.Templates.WebResources.DoesNotExist.txt",
                                                  Path.Combine(_tempDir, "out.txt"));

        await act.Should().ThrowAsync<FlowlineException>();
    }

    [Fact]
    public async Task WriteAsync_WritesContentMatchingEmbeddedResource()
    {
        const string logicalName = "Flowline.Templates.WebResources.package.json";
        var targetPath = Path.Combine(_tempDir, "package.json");

        await TemplateWriter.WriteAsync(logicalName, targetPath);

        var written = await File.ReadAllBytesAsync(targetPath);
        var embedded = ReadEmbeddedResource(logicalName);
        written.Should().Equal(embedded);
    }

    private static byte[] ReadEmbeddedResource(string logicalName)
    {
        using var stream = typeof(TemplateWriter).Assembly.GetManifestResourceStream(logicalName)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
