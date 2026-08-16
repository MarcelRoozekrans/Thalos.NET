using System.Collections.Concurrent;
using ZeroAlloc.Mediator;

namespace Thalos.Testing;

/// <summary>
/// <see cref="IAgentNotificationPublisher"/> that records every notification in memory (thread-safe, in publish order) so tests
/// can assert on the audit trail: <c>publisher.Of&lt;ToolCallDeniedNotification&gt;().Should().ContainSingle()</c>.
/// Register it in place of the default no-op publisher (<c>services.AddSingleton&lt;IAgentNotificationPublisher, RecordingNotificationPublisher&gt;()</c>)
/// or pass it directly to the runtime/catalog constructors.
/// </summary>
public sealed class RecordingNotificationPublisher : IAgentNotificationPublisher
{
    private readonly ConcurrentQueue<INotification> _all = new();

    /// <summary>Every notification published so far, in publish order (a snapshot; later publishes are not reflected).</summary>
    public IReadOnlyList<INotification> All => _all.ToArray();

    /// <summary>The notifications of type <typeparamref name="T"/> published so far, in publish order.</summary>
    public IReadOnlyList<T> Of<T>() where T : INotification => _all.OfType<T>().ToList();

    /// <summary>Forgets every recorded notification.</summary>
    public void Clear() => _all.Clear();

    /// <inheritdoc />
    public ValueTask PublishAsync<TNotification>(TNotification notification, CancellationToken ct) where TNotification : INotification
    {
        _all.Enqueue(notification);
        return default;
    }
}
