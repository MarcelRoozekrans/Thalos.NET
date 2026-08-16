using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.AsyncEvents;

namespace Thalos.Runtime;

/// <summary>
/// In-process fan-out of <see cref="AgentEvent"/>s (channel adapters subscribe here). Handlers run in parallel and are
/// isolated from each other: a handler that throws is logged and does not affect other subscribers or the publisher.
/// Only the publisher's own cancellation (the token passed to <see cref="PublishAsync"/>) propagates; a subscriber's
/// foreign <see cref="OperationCanceledException"/> is treated like any other failure.
/// Thread-safe without locks — <see cref="AsyncEventHandler{TArgs}"/> registers with CAS over immutable arrays and
/// invokes over a snapshot.
/// </summary>
public sealed partial class AgentEventHub(ILogger<AgentEventHub>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<AgentEventHub>.Instance;
    private AsyncEventHandler<AgentEvent> _handlers = new(InvokeMode.Parallel);

    /// <summary>Number of currently registered subscribers.</summary>
    public int SubscriberCount => _handlers.Count;

    /// <summary>
    /// Registers <paramref name="handler"/> for every published event. Dispose the returned token to unsubscribe.
    /// Each call registers an independent subscription (the handler is wrapped for isolation), so subscribing the same
    /// delegate twice yields two deliveries per event until each token is disposed.
    /// </summary>
    public IDisposable Subscribe(AsyncEvent<AgentEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        AsyncEvent<AgentEvent> isolated = async (e, ct) =>
        {
            try
            {
                await handler(e, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                LogSubscriberFailed(_logger, e.Kind, ex.Message, ex);
            }
        };
        _handlers.Register(isolated);
        return new Subscription(this, isolated);
    }

    /// <summary>Delivers <paramref name="agentEvent"/> to all current subscribers in parallel; a no-op with no subscribers.</summary>
    public ValueTask PublishAsync(AgentEvent agentEvent, CancellationToken ct) =>
        _handlers.Count == 0 ? default : _handlers.InvokeAsync(agentEvent, ct);

    private sealed class Subscription(AgentEventHub hub, AsyncEvent<AgentEvent> handler) : IDisposable
    {
        public void Dispose() => hub._handlers.Unregister(handler);
    }

    [LoggerMessage(EventId = 120, Level = LogLevel.Warning, Message = "Agent event subscriber failed for {Kind}: {Error}")]
    private static partial void LogSubscriberFailed(ILogger logger, string kind, string error, Exception exception);
}
