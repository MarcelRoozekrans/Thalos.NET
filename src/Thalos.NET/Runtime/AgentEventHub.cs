using ZeroAlloc.AsyncEvents;

namespace Thalos.Runtime;

/// <summary>In-process fan-out of <see cref="AgentEvent"/>s (channels subscribe here). Parallel, cancellation-aware.</summary>
public sealed class AgentEventHub
{
    private AsyncEventHandler<AgentEvent> _handlers = new(InvokeMode.Parallel);
    private readonly object _gate = new();

    public int SubscriberCount { get { lock (_gate) { return _handlers.Count; } } }

    public IDisposable Subscribe(AsyncEvent<AgentEvent> handler)
    {
        lock (_gate) { _handlers.Register(handler); }
        return new Subscription(this, handler);
    }

    public ValueTask PublishAsync(AgentEvent agentEvent, CancellationToken ct)
    {
        lock (_gate)
        {
            return _handlers.Count == 0 ? default : _handlers.InvokeAsync(agentEvent, ct);
        }
    }

    private sealed class Subscription(AgentEventHub hub, AsyncEvent<AgentEvent> handler) : IDisposable
    {
        public void Dispose() { lock (hub._gate) { hub._handlers.Unregister(handler); } }
    }
}
