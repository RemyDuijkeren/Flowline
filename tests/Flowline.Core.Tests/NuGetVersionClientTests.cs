using System.Net;
using Flowline.Core.Services;
using FluentAssertions;
using Xunit;

namespace Flowline.Core.Tests;

public class NuGetVersionClientTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task GetVersionsAsync_ReturnsVersionsInResponseOrder_WhenIndexIsWellFormed()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"versions":["0.15.0","0.16.0","0.17.0-beta.1"]}"""),
            }));
        var client = new NuGetVersionClient(new HttpClient(handler));

        var versions = await client.GetVersionsAsync("flowline");

        versions.Should().Equal("0.15.0", "0.16.0", "0.17.0-beta.1");
    }

    [Fact]
    public async Task GetVersionsAsync_ReturnsNothing_WhenResponseIs404()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var client = new NuGetVersionClient(new HttpClient(handler));

        var versions = await client.GetVersionsAsync("flowline");

        versions.Should().BeNull();
    }

    [Fact]
    public async Task GetVersionsAsync_ReturnsNothing_WhenResponseIs500()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var client = new NuGetVersionClient(new HttpClient(handler));

        var versions = await client.GetVersionsAsync("flowline");

        versions.Should().BeNull();
    }

    [Fact]
    public async Task GetVersionsAsync_ReturnsNothing_WhenBodyIsNotJson()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") }));
        var client = new NuGetVersionClient(new HttpClient(handler));

        var versions = await client.GetVersionsAsync("flowline");

        versions.Should().BeNull();
    }

    [Fact]
    public async Task GetVersionsAsync_ReturnsNothing_WhenJsonLacksVersionsProperty()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }));
        var client = new NuGetVersionClient(new HttpClient(handler));

        var versions = await client.GetVersionsAsync("flowline");

        versions.Should().BeNull();
    }

    [Fact]
    public async Task GetVersionsAsync_ReturnsNothing_WhenHandlerThrowsHttpRequestException()
    {
        var handler = new FakeHttpMessageHandler((_, _) => throw new HttpRequestException("offline"));
        var client = new NuGetVersionClient(new HttpClient(handler));

        var versions = await client.GetVersionsAsync("flowline");

        versions.Should().BeNull();
    }

    [Fact]
    public async Task GetVersionsAsync_CancelsAndReturnsNothing_WhenHandlerBlocksPastTimeout()
    {
        var handler = new FakeHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new NuGetVersionClient(new HttpClient(handler), TestTimeout);

        var versions = await client.GetVersionsAsync("flowline");

        versions.Should().BeNull();
        handler.LastToken.Should().NotBeNull();
        handler.LastToken!.Value.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task GetVersionsAsync_ReturnsNothingWithoutDispatching_WhenCallerTokenAlreadyCancelled()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var client = new NuGetVersionClient(new HttpClient(handler));

        var versions = await client.GetVersionsAsync("flowline", new CancellationToken(canceled: true));

        versions.Should().BeNull();
        handler.WasInvoked.Should().BeFalse();
    }
}
