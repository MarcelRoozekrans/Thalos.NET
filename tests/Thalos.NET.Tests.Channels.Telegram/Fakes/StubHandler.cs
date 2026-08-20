namespace Thalos.Tests.Channels.Telegram.Fakes;

/// <summary>Answers each request from a queue of canned responses and records what was asked.</summary>
public sealed class StubHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new(responses);

    /// <summary>The path-and-query, followed by the request body (when present), for every request handled so far.</summary>
    public List<string> Requests { get; } = [];

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!.PathAndQuery);
        if (request.Content is not null)
        {
            Requests.Add(await request.Content.ReadAsStringAsync(cancellationToken));
        }

        return _responses.Count > 0 ? _responses.Dequeue() : Json("""{"ok":true,"result":[]}""");
    }

    /// <summary>Builds a canned JSON response with the given body and status code.</summary>
    public static HttpResponseMessage Json(string body, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
}
