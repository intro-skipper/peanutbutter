namespace Jellyfin.Plugin.PeanutButter.Tests.Support;

/// <summary>
/// Scriptable <see cref="HttpMessageHandler"/>: the responder decides each response and
/// every request is recorded (with its headers) for assertions.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<Uri?> RequestedUris { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestedUris.Add(request.RequestUri);
        return Task.FromResult(_responder(request));
    }
}
