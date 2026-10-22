using System.Net;
using System.Text;

namespace Storefront.Api.Tests.TestSupport;

/// <summary>Returns canned HTTP responses so FakeStoreClient can be tested without the network.</summary>
internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    public static StubHttpMessageHandler Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }));

    public static StubHttpMessageHandler Throws(Exception exception) =>
        new((_, _) => Task.FromException<HttpResponseMessage>(exception));

    /// <summary>Never answers until cancelled, which is how HttpClient.Timeout behaves.</summary>
    public static StubHttpMessageHandler Hangs() =>
        new(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return respond(request, cancellationToken);
    }
}
