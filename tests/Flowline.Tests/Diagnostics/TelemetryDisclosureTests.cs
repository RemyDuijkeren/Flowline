using Flowline.Diagnostics;
using Flowline.Validation;
using FluentAssertions;
using Xunit;

namespace Flowline.Tests.Diagnostics;

public class TelemetryDisclosureTests : IDisposable
{
    readonly string _cachePath = Path.Combine(Path.GetTempPath(), $"flowline-disclosure-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_cachePath);

    [Fact]
    public void TheFirstRunWritesTheNotice_AndTheSecondWritesNothing()
    {
        var store = new ValidationCacheStore(_cachePath);

        var first = new StringWriter();
        TelemetryDisclosure.ShowOnce(store, first);
        var second = new StringWriter();
        TelemetryDisclosure.ShowOnce(store, second);

        first.ToString().Should().Contain("Flowline sends usage and crash telemetry");
        second.ToString().Should().BeEmpty();
    }

    [Fact]
    public void TheNoticeNamesTheOptOutVariableAndPointsAtTheLogFile()
    {
        var writer = new StringWriter();

        TelemetryDisclosure.ShowOnce(new ValidationCacheStore(_cachePath), writer);

        var text = writer.ToString();
        text.Should().Contain("FLOWLINE_TELEMETRY_OPTOUT");
        text.Should().Contain("log file");
        text.Should().Contain("exception");
    }

    [Fact]
    public void ShowingTheNoticeMarksItShownInTheCacheAndLeavesTheRestAlone()
    {
        var store = new ValidationCacheStore(_cachePath);
        store.Save(new ValidationCache { WelcomeShownAtUtc = DateTimeOffset.UnixEpoch });

        TelemetryDisclosure.ShowOnce(store, new StringWriter());

        var cache = store.Load();
        cache.TelemetryDisclosureShownAtUtc.Should().NotBeNull();
        cache.WelcomeShownAtUtc.Should().Be(DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void ACacheThatCannotBeWrittenStillGetsTheNoticeAndDoesNotThrow()
    {
        var unwritable = Path.Combine(Path.GetTempPath(), $"flowline-disclosure-{Guid.NewGuid():N}", "\0bad", "cache.json");
        var writer = new StringWriter();

        TelemetryDisclosure.ShowOnce(new ValidationCacheStore(unwritable), writer);

        writer.ToString().Should().Contain("Flowline sends usage and crash telemetry");
    }
}
