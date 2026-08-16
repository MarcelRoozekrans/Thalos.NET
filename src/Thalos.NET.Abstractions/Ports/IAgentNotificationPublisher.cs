using ZeroAlloc.Mediator;

namespace Thalos;

/// <summary>
/// Host-supplied bridge to the application's mediator (ZeroAlloc.Mediator generates an internal IMediator per assembly,
/// so the library cannot publish through it directly). Default implementation is a no-op.
/// </summary>
public interface IAgentNotificationPublisher
{
    ValueTask PublishAsync<TNotification>(TNotification notification, CancellationToken ct) where TNotification : INotification;
}
