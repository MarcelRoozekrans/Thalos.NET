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
/// <para>
/// A scope also carries the turn's token ceiling and its running count, read and fed by the chat-client pipeline's
/// budget check before and after every model round trip. The count belongs to the scope, not to the async flow: a
/// nested scope, such as a subagent's turn begun from inside a tool call of this one, starts at zero with its own
/// ceiling and never adds to the scope it nests in.
/// </para>
/// </remarks>
public sealed class TurnScope : IDisposable
{
    private static readonly AsyncLocal<TurnScope?> _current = new();
    private readonly TurnScope? _previous;
    private readonly ConcurrentQueue<ToolCallSummary> _toolCalls = new();
    private readonly Channel<AgentEvent> _events;
    private int _roundTrips;
    private long _tokens;

    private TurnScope(SessionId sessionId, TurnId turnId, AgentId agentId, ISecurityContext caller, int? maxTotalTokens, TurnScope? previous)
    {
        SessionId = sessionId;
        TurnId = turnId;
        AgentId = agentId;
        Caller = caller;
        MaxTotalTokens = maxTotalTokens;
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

    /// <summary>
    /// The turn's token ceiling: no model round trip starts once <see cref="TokensSoFar"/> is at or above it. Null
    /// when the turn has no ceiling.
    /// </summary>
    internal int? MaxTotalTokens { get; }

    /// <summary>Input plus output tokens the turn's completed model round trips reported so far.</summary>
    internal long TokensSoFar => Interlocked.Read(ref _tokens);

    /// <summary>
    /// Begins a scope on the current async flow and makes it <see cref="Current"/>; dispose to restore the previous scope.
    /// </summary>
    /// <param name="sessionId">The session the turn belongs to.</param>
    /// <param name="turnId">The turn being executed.</param>
    /// <param name="caller">The principal on whose behalf the turn runs.</param>
    /// <param name="agentId">The agent running the turn; default when the scope has none.</param>
    /// <param name="maxTotalTokens">
    /// The turn's token ceiling. Null means the turn has no token ceiling. That is a supported configuration: a chat or
    /// scheduled turn carries no subagent budget, and <see cref="AgentTurnRequest.MaxTotalTokens"/> is null for it
    /// (ruling R27). The new scope's count starts at zero whatever the enclosing scope has spent.
    /// </param>
    internal static TurnScope Begin(SessionId sessionId, TurnId turnId, ISecurityContext caller, AgentId agentId = default, int? maxTotalTokens = null)
    {
        var scope = new TurnScope(sessionId, turnId, agentId, caller, maxTotalTokens, _current.Value);
        _current.Value = scope;
        return scope;
    }

    /// <summary>Numbers the next model round trip of the turn, starting at 1. Safe to call from concurrent round trips.</summary>
    internal int NextRoundTrip() => Interlocked.Increment(ref _roundTrips);

    /// <summary>Adds the tokens one model round trip reported to <see cref="TokensSoFar"/>. Safe to call from concurrent round trips.</summary>
    internal void AddTokens(long n) => Interlocked.Add(ref _tokens, n);

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
