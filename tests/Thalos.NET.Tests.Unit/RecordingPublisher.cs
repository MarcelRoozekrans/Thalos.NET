using System.Collections.Concurrent;
using ZeroAlloc.Mediator;

namespace Thalos.Tests.Unit;

internal sealed class RecordingPublisher : IAgentNotificationPublisher
{
    public ConcurrentQueue<INotification> All { get; } = new();
    public IReadOnlyList<T> Of<T>() where T : INotification => All.OfType<T>().ToList();
    public ValueTask PublishAsync<TNotification>(TNotification notification, CancellationToken ct) where TNotification : INotification
    { All.Enqueue(notification); return default; }
}
