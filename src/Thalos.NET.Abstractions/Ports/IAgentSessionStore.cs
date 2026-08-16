using Microsoft.Extensions.AI;
using ZeroAlloc.Results;
using ZeroAlloc.Telemetry;

namespace Thalos;

/// <summary>
/// Persistence for session headers and chat messages. Implementations must be safe for concurrent use
/// (multiple sessions in parallel; concurrent calls for the same session must not corrupt state).
/// Messages are Microsoft.Extensions.AI <see cref="ChatMessage"/>s; serialize with <c>AIJsonUtilities.DefaultOptions</c>
/// so tool-call/result content round-trips.
/// The contract documented on each member is enforced by the reusable store contract tests in <c>Thalos.NET.Testing</c>.
/// </summary>
[Instrument("thalos", PublicProxy = true)]
public interface IAgentSessionStore
{
    /// <summary>
    /// Creates a new session header. The returned record has <see cref="SessionState.Idle"/>, <c>TurnCount</c> 0,
    /// zero token totals, and <c>CreatedAt == LastActivityAt</c>.
    /// </summary>
    [Trace("thalos.session.create")]
    ValueTask<Result<AgentSessionRecord, AgentError>> CreateAsync(AgentId agentId, string ownerId, CancellationToken ct);

    /// <summary>Returns the session header. Unknown id → <see cref="AgentErrorCode.SessionNotFound"/>.</summary>
    [Trace("thalos.session.get")]
    ValueTask<Result<AgentSessionRecord, AgentError>> GetAsync(SessionId id, CancellationToken ct);

    /// <summary>
    /// Lists sessions owned by <paramref name="ownerId"/>, newest first by <c>CreatedAt</c>, paged with
    /// <paramref name="skip"/>/<paramref name="take"/>. Unknown owner → empty list (not an error).
    /// </summary>
    [Trace("thalos.session.list")]
    ValueTask<Result<IReadOnlyList<AgentSessionRecord>, AgentError>> ListAsync(string ownerId, int skip, int take, CancellationToken ct);

    /// <summary>Loads all messages in append order. Unknown id → <see cref="AgentErrorCode.SessionNotFound"/>; a known session with no messages → empty list.</summary>
    [Trace("thalos.session.messages.load")]
    ValueTask<Result<IReadOnlyList<ChatMessage>, AgentError>> LoadMessagesAsync(SessionId id, CancellationToken ct);

    /// <summary>
    /// Appends messages in the given order (order is preserved across calls). Called by the chat-history provider after
    /// every model round-trip. Unknown id → <see cref="AgentErrorCode.SessionNotFound"/>.
    /// </summary>
    [Trace("thalos.session.messages.append")]
    ValueTask<UnitResult<AgentError>> AppendMessagesAsync(SessionId id, IReadOnlyList<ChatMessage> messages, CancellationToken ct);

    /// <summary>
    /// Increments <c>TurnCount</c>, adds <paramref name="usage"/> to the token totals and bumps <c>LastActivityAt</c>.
    /// Unknown id → <see cref="AgentErrorCode.SessionNotFound"/>.
    /// </summary>
    [Trace("thalos.session.turn.record")]
    ValueTask<UnitResult<AgentError>> RecordTurnAsync(SessionId id, TurnUsage usage, CancellationToken ct);

    /// <summary>Sets the session state (no transition validation — that is the runtime's job). Unknown id → <see cref="AgentErrorCode.SessionNotFound"/>.</summary>
    [Trace("thalos.session.state.update")]
    ValueTask<UnitResult<AgentError>> UpdateStateAsync(SessionId id, SessionState state, CancellationToken ct);
}
