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
    public void TheNoticeNamesWhatNobodyWouldAssumeAndPointsAtTheRest()
    {
        var writer = new StringWriter();

        TelemetryDisclosure.ShowOnce(new ValidationCacheStore(_cachePath), writer);

        var text = writer.ToString();
        // The two things nobody assumes from the word "telemetry", and the route to everything else.
        text.Should().Contain("log lines");
        text.Should().Contain("stack trace");
        text.Should().Contain("log file", "the claim that the local log is the payload is the verifiable one");
        text.Should().Contain("wiki/18-Telemetry", "a one-off notice cannot carry the whole story");
        text.Should().NotContain("anonym", "a salted hash is pseudonymisation, not anonymisation");
        text.Should().NotContain("—", "em dashes are out of house style");
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Should().HaveCountLessThanOrEqualTo(3, "a first-run notice nobody reads is worth nothing");
    }

    [Fact]
    public void TheNoticeShowsAgainAfterAnUpdate()
    {
        // An update is the moment collection can widen without the user noticing, so it is the moment
        // to say so again. The dotnet CLI keys its own first-use sentinel to the SDK version the same
        // way.
        var store = new ValidationCacheStore(_cachePath);
        store.Save(new ValidationCache { TelemetryDisclosureVersion = "0.0.1-older" });

        var writer = new StringWriter();
        TelemetryDisclosure.ShowOnce(store, writer);

        writer.ToString().Should().Contain("Flowline sends usage and crash telemetry");
        store.Load().TelemetryDisclosureVersion.Should().NotBe("0.0.1-older");
    }

    [Fact]
    public void TheNoticeStaysQuietOnTheSameVersion()
    {
        var store = new ValidationCacheStore(_cachePath);
        TelemetryDisclosure.ShowOnce(store, new StringWriter());

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
        cache.TelemetryDisclosureVersion.Should().NotBeNullOrWhiteSpace();
        cache.WelcomeShownAtUtc.Should().Be(DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void AStderrThatThrowsLeavesTheNoticeUnshown_SoTheNextRunStillTellsTheUser()
    {
        var store = new ValidationCacheStore(_cachePath);

        TelemetryDisclosure.ShowOnce(store, new ThrowingWriter());

        store.Load().TelemetryDisclosureVersion.Should().BeNull(
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
