using System.Net;
using System.Text;

namespace LViz.Tests.GitHub;

/// <summary>
/// Scripted <see cref="HttpMessageHandler"/> so GitHub tests never touch the
/// network. Set <see cref="Respond"/> (a single response, or one chosen from
/// the request); every request lands in <see cref="Requests"/> with its body
/// already read as text — the body has to be captured before responding,
/// because <see cref="HttpClient"/> disposes the request content afterwards.
/// Set <see cref="Throw"/> to simulate a transport failure.
/// </summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    public FakeHttpHandler()
    {
        Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
    }

    /// <summary>Produces the answer for a request. Defaults to HTTP 500.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; }

    /// <summary>Everything that was sent, in order, with the request body.</summary>
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();

    /// <summary>When set, every send throws this instead of answering.</summary>
    public Exception? Throw { get; set; }

    /// <summary>A response with a <c>application/json</c> body.</summary>
    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>A response with a <c>text/plain</c> body.</summary>
    public static HttpResponseMessage Text(string text, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string? body = null;
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        Requests.Add((request, body));

        if (Throw is not null)
        {
            throw Throw;
        }

        return Respond(request);
    }
}
