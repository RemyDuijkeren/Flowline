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

    [Fact]
    public void LoadOrCreate_DifferentPaths_ProduceDifferentSalts()
    {
        var storeA = new TelemetrySaltStore(Path.Combine(_tempDir, "a", "telemetry-salt"));
        var storeB = new TelemetrySaltStore(Path.Combine(_tempDir, "b", "telemetry-salt"));

        storeA.LoadOrCreate().Should().NotEqual(storeB.LoadOrCreate());
    }
}
