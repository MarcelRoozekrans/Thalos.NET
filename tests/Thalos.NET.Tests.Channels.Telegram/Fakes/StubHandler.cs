namespace Thalos.Tests.Channels.Telegram.Fakes;

/// <summary>Answers each request from a queue of canned responses and records what was asked.</summary>
public sealed class StubHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new(responses);

    private string? _blockMarker;
    private TaskCompletionSource? _blockReached;
    private TaskCompletionSource? _blockRelease;

    /// <summary>
    /// The path-and-query, followed by the request body (when present), for every request handled so far.
    /// Read it from one thread, or under <c>lock (Requests)</c> — <see cref="BlockNextRequestContaining"/> exists
    /// precisely so a test can have two requests in flight at once.
    /// </summary>
    public List<string> Requests { get; } = [];

    /// <summary>
    /// Holds the NEXT request whose body contains <paramref name="marker"/> open until the returned source is
    /// completed, so a test can prove something else happens while that call is still outstanding. The response is
    /// dequeued before the block, so queue order still matches the order requests were issued in.
    /// </summary>
    /// <returns>
    /// A task that completes once the matching request has arrived and is being held, and the source that releases
    /// it. Callers must complete the source (ideally in a <c>finally</c>) even on test failure, or the held request
    /// never returns.
    /// </returns>
    public (Task Reached, TaskCompletionSource Release) BlockNextRequestContaining(string marker)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (Requests)
        {
            _blockMarker = marker;
            _blockReached = reached;
            _blockRelease = release;
        }

        return (reached.Task, release);
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Read the body before taking the lock: awaiting inside a lock is not allowed, and the content is not
        // shared with any other request.
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        HttpResponseMessage response;
        TaskCompletionSource? reached = null;
        TaskCompletionSource? release = null;

        lock (Requests)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            if (body is not null)
            {
                Requests.Add(body);
            }

            response = _responses.Count > 0 ? _responses.Dequeue() : Json("""{"ok":true,"result":[]}""");

            if (_blockMarker is { } marker && body is not null && body.Contains(marker, StringComparison.Ordinal))
            {
                // One-shot: only the first match is held, so a later identical call cannot deadlock the test.
                _blockMarker = null;
                reached = _blockReached;
                release = _blockRelease;
            }
        }

        if (reached is not null && release is not null)
        {
            reached.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        }

        return response;
    }

    /// <summary>Builds a canned JSON response with the given body and status code.</summary>
    public static HttpResponseMessage Json(string body, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
}
