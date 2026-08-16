using ZeroAlloc.Mediator;

namespace Thalos;

/// <summary>
/// Host-supplied bridge to the application's mediator (ZeroAlloc.Mediator generates an internal IMediator per assembly,
/// so the library cannot publish through it directly). Default implementation is a no-op.
/// </summary>
public interface IAgentNotificationPublisher
{
    /// <summary>
    /// Publishes one lifecycle/audit notification (session created/closed, turn started/completed/failed, tool call
    /// requested/denied/completed). Called inline on the hot path — implementations must be non-blocking (queue and return)
    /// and thread-safe. Exceptions thrown before a turn is persisted fail the turn; exceptions thrown for post-persist
    /// notifications are logged and swallowed by the runtime.
    /// </summary>
    ValueTask PublishAsync<TNotification>(TNotification notification, CancellationToken ct) where TNotification : INotification;
}
