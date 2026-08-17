using System.Collections.Concurrent;
using System.Threading.Channels;
using ZeroAlloc.Authorization;

namespace Thalos.Runtime;

/// <summary>
/// Ambient context of the turn currently executing on this async flow. Carries the session, turn and caller into
/// code that receives no parameters (e.g. the authorizing tool wrapper running inside MAF's function-invocation
/// pipeline), collects tool-call summaries, and streams tool events back to the runtime through <see cref="Events"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only the runtime begins, feeds and disposes scopes (those members are internal); the public surface is read-only —
/// <see cref="Current"/>, <see cref="SessionId"/>, <see cref="TurnId"/>, <see cref="AgentId"/>, <see cref="Caller"/>, <see cref="Events"/>,
/// <see cref="ToolCalls"/> — plus <see cref="PublishAsync"/> for extensions that raise their own events inside the turn.
/// Tools and decorators may read <see cref="Current"/> to learn who is calling; they must never dispose it.
/// </para>
/// <para>
/// Scopes are LIFO: <c>Begin</c> captures the previous scope and <c>Dispose</c> restores it, so a scope
/// must be disposed on the same async flow that began it (use <c>using</c>).
/// </para>
/// <para>
/// An <see cref="AsyncLocal{T}"/> scope does <b>not</b> survive a <c>yield return</c> inside an async iterator: the
/// execution context is restored on each resumption of the enumerator, so <see cref="Current"/> is null again after
/// the first yield. The runtime therefore runs the model loop in a producer <see cref="Task"/> that owns the scope and
/// drains <see cref="Events"/> from the consuming iterator.
/// </para>
/// </remarks>
public sealed class TurnScope : IDisposable
{
    private static readonly AsyncLocal<TurnScope?> _current = new();
    private readonly TurnScope? _previous;
    private readonly ConcurrentQueue<ToolCallSummary> _toolCalls = new();
    private readonly Channel<AgentEvent> _events;

    private TurnScope(SessionId sessionId, TurnId turnId, AgentId agentId, ISecurityContext caller, TurnScope? previous)
    {
        SessionId = sessionId;
        TurnId = turnId;
        AgentId = agentId;
        Caller = caller;
        _previous = previous;
        _events = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });
    }

    /// <summary>The scope of the turn executing on the current async flow, or null when no turn is in progress.</summary>
    public static TurnScope? Current => _current.Value;

    /// <summary>The session the turn belongs to.</summary>
    public SessionId SessionId { get; }

    /// <summary>The turn being executed.</summary>
    public TurnId TurnId { get; }

    /// <summary>The agent running the turn (default when the scope was begun without one).</summary>
    public AgentId AgentId { get; }

    /// <summary>The principal on whose behalf the turn runs; tool authorization is evaluated against it.</summary>
    public ISecurityContext Caller { get; }

    /// <summary>
    /// Tool events raised inside the turn, in publish order; the runtime drains this into the streaming output.
    /// Completed by <see cref="Dispose"/>.
    /// </summary>
    public ChannelReader<AgentEvent> Events => _events.Reader;

    /// <summary>Summaries recorded with <see cref="RecordToolCall"/> so far, in completion order.</summary>
    public IReadOnlyCollection<ToolCallSummary> ToolCalls => _toolCalls;

    /// <summary>Begins a scope on the current async flow and makes it <see cref="Current"/>; dispose to restore the previous scope.</summary>
    internal static TurnScope Begin(SessionId sessionId, TurnId turnId, ISecurityContext caller, AgentId agentId = default)
    {
        var scope = new TurnScope(sessionId, turnId, agentId, caller, _current.Value);
        _current.Value = scope;
        return scope;
    }

    /// <summary>
    /// Queues an event for the runtime (streamed to the caller and fanned out to <see cref="AgentEventHub"/>). Extensions such as
    /// Thalos.NET.Memory publish their own <see cref="AgentEvent"/>s here. Never throws once the scope is disposed: events
    /// published after the consumer abandoned the stream (e.g. a tool still running after cancellation) are dropped silently.
    /// </summary>
    public ValueTask PublishAsync(AgentEvent agentEvent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _events.Writer.TryWrite(agentEvent);
        return default;
    }

    /// <summary>Records the outcome of one tool call for the turn result.</summary>
    internal void RecordToolCall(ToolCallSummary summary) => _toolCalls.Enqueue(summary);

    /// <summary>Completes <see cref="Events"/> and restores the previous scope as <see cref="Current"/>.</summary>
    void IDisposable.Dispose() => Dispose();

    /// <summary>Completes <see cref="Events"/> and restores the previous scope as <see cref="Current"/>.</summary>
    internal void Dispose()
    {
        _events.Writer.TryComplete();
        _current.Value = _previous;
    }
}
