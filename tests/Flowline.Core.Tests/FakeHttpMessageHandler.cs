namespace Flowline.Core.Tests;

/// <summary>Test double for <see cref="HttpMessageHandler"/>. Records whether it was invoked and
/// the cancellation token it was invoked with, then delegates to <paramref name="responder"/>.</summary>
public sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    : HttpMessageHandler
{
    public bool WasInvoked { get; private set; }
    public CancellationToken? LastToken { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        WasInvoked = true;
        LastToken = cancellationToken;
        return responder(request, cancellationToken);
    }
}
