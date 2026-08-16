using ZeroAlloc.Mediator;

namespace Thalos.Runtime;

public sealed class NullAgentNotificationPublisher : IAgentNotificationPublisher
{
    public static NullAgentNotificationPublisher Instance { get; } = new();
    public ValueTask PublishAsync<TNotification>(TNotification notification, CancellationToken ct) where TNotification : INotification => default;
}
