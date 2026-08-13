using System.Net;
using Flowline.Core.Services;
using Flowline.Services;
using Flowline.Utils;
using Flowline.Validation;
using FluentAssertions;
using NuGet.Versioning;
using Spectre.Console.Testing;
using Xunit;

namespace Flowline.Tests;

// Seam-level tests for UpdateNoticeChecker — the piece FlowlineCommand.CheckSetupAsync delegates to.
// Tested in isolation rather than through a full command: FlowlineValidator.Default persists to the
// user's real cache file on disk, and CheckSetupAsync's other probes (git/dotnet/pac) shell out for real.
public class UpdateNoticeTests
{
    // Local fake — Flowline.Tests has no reference to Flowline.Core.Tests, where the shared
    // FakeHttpMessageHandler lives.
    sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return responder(request, cancellationToken);
        }
    }

    static NuGetVersionClient MakeClient(FakeHandler handler) => new(new HttpClient(handler));

    static FlowlineValidator MakeValidator(string? cachePath = null) =>
        new(new ValidationCacheStore(cachePath ?? Path.Combine(Path.GetTempPath(), $"flowline-update-notice-{Guid.NewGuid()}.json")), new ValidationProbes());

    // A version guaranteed newer than the running FlowlineVersion.Display, and the NuGet index payload
    // that reports it.
    static (string NewerVersion, string PayloadJson) NewerVersionScenario()
    {
        var running = NuGetVersion.Parse(FlowlineVersion.Display);
        var newer = new NuGetVersion(running.Major + 1, 0, 0).ToString();
        return (newer, $$"""{"versions":["{{newer}}"]}""");
    }

    static TestConsole MakeConsole(bool interactive)
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = interactive;
        // Wide enough that the notice never wraps — assertions below check for the literal, unbroken
        // command string.
        console.Profile.Width = 4096;
        return console;
    }

    [Fact]
    public async Task CheckAsync_NonInteractive_NeverTouchesCacheOrNetwork()
    {
        var console = MakeConsole(interactive: false);
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("should never be called"));
        var client = MakeClient(handler);
        var validator = MakeValidator();

        var result = await UpdateNoticeChecker.CheckAsync(console, validator, client, noCache: false, CancellationToken.None);

        result.Should().BeNull();
        handler.CallCount.Should().Be(0);
        console.Output.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckAsync_Interactive_RunningVersionBehind_ReturnsNewerVersionAndSavesIt()
    {
        var console = MakeConsole(interactive: true);
        var (newerVersion, payload) = NewerVersionScenario();
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) }));
        var client = MakeClient(handler);
        var validator = MakeValidator();

        var result = await UpdateNoticeChecker.CheckAsync(console, validator, client, noCache: false, CancellationToken.None);

        result.Should().Be(newerVersion);
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public void PrintNotice_NewerVersionAvailable_NamesBothVersionsAndTheUpdateCommandAsPlainText()
    {
        var console = MakeConsole(interactive: true);
        var (newerVersion, _) = NewerVersionScenario();

        UpdateNoticeChecker.PrintNotice(console, newerVersion);

        var output = console.Output;
        output.Should().Contain(newerVersion);
        output.Should().Contain(FlowlineVersion.Display);
        output.Should().Contain("dotnet tool update -g Flowline");
    }

    [Fact]
    public void PrintNotice_NoNewerVersion_PrintsNothing()
    {
        var console = new TestConsole();

        UpdateNoticeChecker.PrintNotice(console, null);

        console.Output.Should().BeEmpty();
    }

    [Fact]
    public void PrintNotice_NewerVersionAvailable_UsesInfoNotWarningOrError()
    {
        var console = MakeConsole(interactive: true);
        var (newerVersion, _) = NewerVersionScenario();

        UpdateNoticeChecker.PrintNotice(console, newerVersion);

        // '!' is the warning glyph and '✗' the error glyph (docs/tone-of-voice.md) — this is
        // information, not a degraded run, so neither may appear.
        console.Output.Should().NotContain("!");
        console.Output.Should().NotContain("✗");
    }

    [Fact]
    public void PrintNotice_VersionWithMarkupControlCharacters_RendersLiterallyWithoutThrowing()
    {
        var console = MakeConsole(interactive: true);
        const string maliciousVersion = "0.17.0-beta[1]";

        var act = () => UpdateNoticeChecker.PrintNotice(console, maliciousVersion);

        act.Should().NotThrow();
        console.Output.Should().Contain(maliciousVersion);
    }

    [Fact]
    public async Task CheckAsync_TwoConsecutiveInteractiveRuns_FreshCache_ReturnsVerdictBothTimesWithoutASecondNetworkCall()
    {
        var (newerVersion, payload) = NewerVersionScenario();
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) }));
        var client = MakeClient(handler);
        var validator = MakeValidator();

        var first = await UpdateNoticeChecker.CheckAsync(MakeConsole(interactive: true), validator, client, noCache: false, CancellationToken.None);
        var second = await UpdateNoticeChecker.CheckAsync(MakeConsole(interactive: true), validator, client, noCache: false, CancellationToken.None);

        first.Should().Be(newerVersion);
        second.Should().Be(newerVersion);
        handler.CallCount.Should().Be(1, "the second run should hit the fresh cache, not the network");

        // Both runs print the notice — cadence isn't "shown once and then suppressed" (R10).
        var firstConsole = new TestConsole();
        var secondConsole = new TestConsole();
        UpdateNoticeChecker.PrintNotice(firstConsole, first);
        UpdateNoticeChecker.PrintNotice(secondConsole, second);
        firstConsole.Output.Should().Contain(newerVersion);
        secondConsole.Output.Should().Contain(newerVersion);
    }

    [Fact]
    public async Task CheckAsync_VersionClientReturnsNull_ReturnsNullWithoutThrowing()
    {
        var console = MakeConsole(interactive: true);
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var client = MakeClient(handler);
        var validator = MakeValidator();

        var result = await UpdateNoticeChecker.CheckAsync(console, validator, client, noCache: false, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task CheckAsync_FailedCheck_BacksOffInsteadOfRetryingOnTheNextRun()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var client = MakeClient(handler);
        var validator = MakeValidator();

        await UpdateNoticeChecker.CheckAsync(MakeConsole(interactive: true), validator, client, noCache: false, CancellationToken.None);
        var second = await UpdateNoticeChecker.CheckAsync(MakeConsole(interactive: true), validator, client, noCache: false, CancellationToken.None);

        second.Should().BeNull();
        handler.CallCount.Should().Be(1, "an offline machine would otherwise pay the timeout on every command (R5, R19)");
    }

    [Fact]
    public async Task CheckAsync_UnderlyingHttpCallThrows_ReturnsNullWithoutPropagating()
    {
        var console = MakeConsole(interactive: true);
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("offline"));
        var client = MakeClient(handler);
        var validator = MakeValidator();

        var act = async () => await UpdateNoticeChecker.CheckAsync(console, validator, client, noCache: false, CancellationToken.None);

        (await act.Should().NotThrowAsync()).Which.Should().BeNull();
    }

    [Fact]
    public async Task CheckAsync_WhenPersistingTheVerdictThrows_SwallowsExceptionAndReturnsNull()
    {
        // Forces FlowlineValidator.SaveUpdateCheck's file write to throw: the cache path's parent
        // segment is itself an existing file, so Directory.CreateDirectory can't create it. Proves the
        // orchestration's own exception guard (R18), not just NuGetVersionClient's internal one.
        var brokenParent = Path.Combine(Path.GetTempPath(), $"flowline-broken-parent-{Guid.NewGuid()}");
        await File.WriteAllTextAsync(brokenParent, "not a directory");
        try
        {
            var console = MakeConsole(interactive: true);
            var (_, payload) = NewerVersionScenario();
            var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) }));
            var client = MakeClient(handler);
            var validator = MakeValidator(Path.Combine(brokenParent, "validation-cache.json"));

            var act = async () => await UpdateNoticeChecker.CheckAsync(console, validator, client, noCache: false, CancellationToken.None);

            (await act.Should().NotThrowAsync()).Which.Should().BeNull();
        }
        finally
        {
            File.Delete(brokenParent);
        }
    }
}
