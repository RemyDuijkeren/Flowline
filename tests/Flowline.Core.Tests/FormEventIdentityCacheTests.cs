using Flowline.Core.Services;
using Flowline.Core.FormEvents.Support;
using FluentAssertions;
using Xunit;

namespace Flowline.Core.Tests;

public class FormEventIdentityCacheTests : IDisposable
{
    readonly string _tempDir = Path.Combine(Path.GetTempPath(), "flowline-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Set_ThenTryGet_ReturnsStoredFormId()
    {
        var cache = new FormEventIdentityCache(Path.Combine(_tempDir, "cache.json"));
        var formId = Guid.NewGuid();

        cache.Set("account", "Main Form", formId);

        cache.TryGet("account", "Main Form").Should().Be(formId);
    }

    [Fact]
    public void TryGet_UnsetKey_ReturnsNull()
    {
        var cache = new FormEventIdentityCache(Path.Combine(_tempDir, "cache.json"));

        cache.TryGet("account", "Main Form").Should().BeNull();
    }

    [Fact]
    public void Set_TwiceForSameKey_OverwritesRatherThanDuplicates()
    {
        var cache = new FormEventIdentityCache(Path.Combine(_tempDir, "cache.json"));
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        cache.Set("account", "Main Form", firstId);
        cache.Set("account", "Main Form", secondId);

        cache.TryGet("account", "Main Form").Should().Be(secondId);
    }

    [Fact]
    public void TryGet_NoCacheFileExists_ReturnsNullWithoutThrowing()
    {
        var cache = new FormEventIdentityCache(Path.Combine(_tempDir, "does-not-exist.json"));

        var act = () => cache.TryGet("account", "Main Form");

        act.Should().NotThrow();
        act().Should().BeNull();
    }

    [Fact]
    public void TryGet_CacheFileIsEmpty_ReturnsNullWithoutThrowing()
    {
        var path = Path.Combine(_tempDir, "cache.json");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(path, "");
        var cache = new FormEventIdentityCache(path);

        var act = () => cache.TryGet("account", "Main Form");

        act.Should().NotThrow();
        act().Should().BeNull();
    }

    [Fact]
    public void TryGet_CacheFileContainsInvalidJson_ReturnsNullWithoutThrowing()
    {
        var path = Path.Combine(_tempDir, "cache.json");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(path, "{ not valid json ]");
        var cache = new FormEventIdentityCache(path);

        var act = () => cache.TryGet("account", "Main Form");

        act.Should().NotThrow();
        act().Should().BeNull();
    }

    [Fact]
    public void Set_WhenWriteDirectoryCannotBeCreated_DoesNotThrow()
    {
        // A file already occupies the path where the cache's parent directory would need to be created.
        var blockingFilePath = Path.Combine(Path.GetTempPath(), "flowline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(blockingFilePath)!);
        File.WriteAllText(blockingFilePath, "blocking file");
        try
        {
            var cachePath = Path.Combine(blockingFilePath, "cache.json");
            var cache = new FormEventIdentityCache(cachePath);

            var act = () => cache.Set("account", "Main Form", Guid.NewGuid());

            act.Should().NotThrow();
        }
        finally
        {
            File.Delete(blockingFilePath);
        }
    }

    [Fact]
    public void Set_FromOneInstance_IsVisibleToTryGetOnFreshInstanceAtSamePath()
    {
        var path = Path.Combine(_tempDir, "cache.json");
        var formId = Guid.NewGuid();
        var writer = new FormEventIdentityCache(path);
        writer.Set("account", "Main Form", formId);

        var reader = new FormEventIdentityCache(path);

        reader.TryGet("account", "Main Form").Should().Be(formId);
    }
}
