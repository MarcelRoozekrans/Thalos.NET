using System.Collections.Concurrent;
using System.Threading.Channels;
using ZeroAlloc.Authorization;

namespace Thalos.Runtime;

/// <summary>Ambient context of the turn currently executing on this async flow.</summary>
public sealed class TurnScope : IDisposable
{
    private static readonly AsyncLocal<TurnScope?> _current = new();
    private readonly TurnScope? _previous;
    private readonly ConcurrentQueue<ToolCallSummary> _toolCalls = new();

    private TurnScope(SessionId sessionId, TurnId turnId, ISecurityContext caller, TurnScope? previous)
    {
        SessionId = sessionId;
        TurnId = turnId;
        Caller = caller;
        _previous = previous;
        Events = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });
    }

    public static TurnScope? Current => _current.Value;

    public SessionId SessionId { get; }
    public TurnId TurnId { get; }
    public ISecurityContext Caller { get; }

    /// <summary>Tool events raised inside the turn; the runtime drains this into the streaming output.</summary>
    public Channel<AgentEvent> Events { get; }

    public IReadOnlyCollection<ToolCallSummary> ToolCalls => _toolCalls;

    public static TurnScope Begin(SessionId sessionId, TurnId turnId, ISecurityContext caller)
    {
        var scope = new TurnScope(sessionId, turnId, caller, _current.Value);
        _current.Value = scope;
        return scope;
    }

    public ValueTask PublishAsync(AgentEvent agentEvent, CancellationToken ct) => Events.Writer.WriteAsync(agentEvent, ct);

    public void RecordToolCall(ToolCallSummary summary) => _toolCalls.Enqueue(summary);

    public void Dispose()
    {
        Events.Writer.TryComplete();
        _current.Value = _previous;
    }
}
