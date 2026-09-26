using Flowline.Core.Validation;
using Flowline.Diagnostics;
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

        first.ToString().Should().Contain("Flowline CLI collects usage data");
        second.ToString().Should().BeEmpty();
    }

    [Fact]
    public void TheNoticeNamesWhatNobodyWouldAssumeAndPointsAtTheRest()
    {
        var writer = new StringWriter();

        TelemetryDisclosure.ShowOnce(new ValidationCacheStore(_cachePath), writer);

        var text = writer.ToString();
        text.TrimStart().Should().StartWith("!", "it is a heads-up the reader may want to act on, not neutral detail");
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

        writer.ToString().Should().Contain("Flowline CLI collects usage data");
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
    public void TheNoticeIsSetOffFromWhateverThePrintedBeforeIt()
    {
        var writer = new StringWriter();

        TelemetryDisclosure.ShowOnce(new ValidationCacheStore(_cachePath), writer);

        writer.ToString().Should().StartWith(Environment.NewLine,
            "it lands under the command's own last line, usually the finish line");
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
        second.ToString().Should().Contain("Flowline CLI collects usage data");
    }

    sealed class ThrowingWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("stderr is gone");
    }

    [Fact]
    public void TheNoticeCarriesNoEscapeCodesWhenStderrIsNotATerminal()
    {
        var writer = new StringWriter();

        TelemetryDisclosure.ShowOnce(new ValidationCacheStore(_cachePath), writer);

        writer.ToString().Should().NotContain("\u001b", "a CI log should not collect escape codes");
    }

    [Fact]
    public void TheNoticeIsYellowOnATerminal()
    {
        var writer = new StringWriter();

        TelemetryDisclosure.ShowOnce(new ValidationCacheStore(_cachePath), writer, useColour: true);

        writer.ToString().TrimStart().Should()
            .StartWith("\u001b[33m").And.EndWithEquivalentOf("\u001b[0m" + Environment.NewLine);
    }

    [Fact]
    public void ACacheThatCannotBeWrittenStillGetsTheNoticeAndDoesNotThrow()
    {
        var unwritable = Path.Combine(Path.GetTempPath(), $"flowline-disclosure-{Guid.NewGuid():N}", "\0bad", "cache.json");
        var writer = new StringWriter();

        TelemetryDisclosure.ShowOnce(new ValidationCacheStore(unwritable), writer);

        writer.ToString().Should().Contain("Flowline CLI collects usage data");
    }
}
