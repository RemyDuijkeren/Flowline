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
        text.Should().Contain("wiki/18-Telemetry", "a one-off notice cannot carry the whole story");
        text.Should().NotContain("—", "em dashes are out of house style");
    }

    [Fact]
    public void TheNoticeShowsAgainWhenWhatIsCollectedChanges()
    {
        // The dotnet CLI re-shows its own notice on every SDK version. Keying to the collection rather
        // than the release means a user who agreed to a command name and an exit code is asked again
        // before their log lines start being sent, and left alone by a release that collects nothing
        // new.
        var store = new ValidationCacheStore(_cachePath);
        store.Save(new ValidationCache { TelemetryDisclosureVersion = TelemetryDisclosure.Version - 1 });

        var writer = new StringWriter();
        TelemetryDisclosure.ShowOnce(store, writer);

        writer.ToString().Should().Contain("Flowline sends usage and crash telemetry");
        store.Load().TelemetryDisclosureVersion.Should().Be(TelemetryDisclosure.Version);
    }

    [Fact]
    public void TheNoticeStaysQuietForAReleaseThatCollectsNothingNew()
    {
        var store = new ValidationCacheStore(_cachePath);
        store.Save(new ValidationCache { TelemetryDisclosureVersion = TelemetryDisclosure.Version });

        var writer = new StringWriter();
        TelemetryDisclosure.ShowOnce(store, writer);

        writer.ToString().Should().BeEmpty();
    }

    [Fact]
    public void ShowingTheNoticeMarksItShownInTheCacheAndLeavesTheRestAlone()
    {
        var store = new ValidationCacheStore(_cachePath);
        store.Save(new ValidationCache { WelcomeShownAtUtc = DateTimeOffset.UnixEpoch });

        TelemetryDisclosure.ShowOnce(store, new StringWriter());

        var cache = store.Load();
        cache.TelemetryDisclosureShownAtUtc.Should().NotBeNull();
        cache.TelemetryDisclosureVersion.Should().Be(TelemetryDisclosure.Version);
        cache.WelcomeShownAtUtc.Should().Be(DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void AStderrThatThrowsLeavesTheNoticeUnshown_SoTheNextRunStillTellsTheUser()
    {
        var store = new ValidationCacheStore(_cachePath);

        TelemetryDisclosure.ShowOnce(store, new ThrowingWriter());

        store.Load().TelemetryDisclosureVersion.Should().Be(0,
            "a run that could not tell the user must not record that it did");
        var second = new StringWriter();
        TelemetryDisclosure.ShowOnce(store, second);
        second.ToString().Should().Contain("Flowline sends usage and crash telemetry");
    }

    sealed class ThrowingWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("stderr is gone");
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
