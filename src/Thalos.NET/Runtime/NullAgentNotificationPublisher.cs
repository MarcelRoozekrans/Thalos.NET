using ZeroAlloc.Mediator;

namespace Thalos.Runtime;

/// <summary>Default <see cref="IAgentNotificationPublisher"/> that discards every notification; used when the host does not bridge to a mediator.</summary>
public sealed class NullAgentNotificationPublisher : IAgentNotificationPublisher
{
    /// <summary>The shared instance.</summary>
    public static NullAgentNotificationPublisher Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask PublishAsync<TNotification>(TNotification notification, CancellationToken ct) where TNotification : INotification => default;
}
