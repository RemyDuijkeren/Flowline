using Flowline.Logging;
using FluentAssertions;
using Xunit;

namespace Flowline.Tests;

public class TelemetrySaltStoreTests : IDisposable
{
    readonly string _tempDir = Path.Combine(Path.GetTempPath(), "flowline-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void LoadOrCreate_FirstCall_Creates32ByteSalt()
    {
        var store = new TelemetrySaltStore(Path.Combine(_tempDir, "telemetry-salt"));

        var salt = store.LoadOrCreate();

        salt.Should().HaveCount(32);
    }

    [Fact]
    public void LoadOrCreate_SecondCall_ReturnsSamePersistedSalt()
    {
        var store = new TelemetrySaltStore(Path.Combine(_tempDir, "telemetry-salt"));

        var first = store.LoadOrCreate();
        var second = store.LoadOrCreate();

        second.Should().Equal(first);
    }

    // The failure modes that decide whether telemetry can hash at all. A store that throws here leaves
    // the scrubber on its empty-salt default, which is the state FlowlineTelemetry refuses to export in.

    [Fact]
    public void LoadOrCreate_CorruptSaltFile_RegeneratesInsteadOfThrowing()
    {
        var path = Path.Combine(_tempDir, "telemetry-salt");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(path, "not hex at all");

        var salt = new TelemetrySaltStore(path).LoadOrCreate();

        salt.Should().HaveCount(32);
        new TelemetrySaltStore(path).LoadOrCreate().Should().Equal(salt, "the regenerated salt is persisted");
    }

    [Fact]
    public void LoadOrCreate_EmptySaltFile_RegeneratesInsteadOfReturningNothing()
    {
        var path = Path.Combine(_tempDir, "telemetry-salt");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(path, "");

        var salt = new TelemetrySaltStore(path).LoadOrCreate();

        salt.Should().HaveCount(32, "an empty file is not a salt, and a zero-length key hashes nothing");
    }

    [Fact]
    public void LoadOrCreate_UnwritableLocation_ReturnsAUsableSaltRatherThanThrowing()
    {
        // A read-only or otherwise unusable storage root must not take the whole launch with it: the
        // scrubber is built inside the same swallowed block as the logger.
        Directory.CreateDirectory(_tempDir);
        var blocker = Path.Combine(_tempDir, "blocker");
        File.WriteAllText(blocker, "a file where a directory would have to be");
        var path = Path.Combine(blocker, "nested", "telemetry-salt");

        var salt = new TelemetrySaltStore(path).LoadOrCreate();

        salt.Should().HaveCount(32, "the run still needs a working salt, it just cannot keep this one");
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void LoadOrCreate_DifferentPaths_ProduceDifferentSalts()
    {
        var storeA = new TelemetrySaltStore(Path.Combine(_tempDir, "a", "telemetry-salt"));
        var storeB = new TelemetrySaltStore(Path.Combine(_tempDir, "b", "telemetry-salt"));

        storeA.LoadOrCreate().Should().NotEqual(storeB.LoadOrCreate());
    }
}
