using Microsoft.Extensions.AI;
using ZeroAlloc.Results;
using ZeroAlloc.Telemetry;

namespace Thalos;

/// <summary>
/// Persistence for session headers and chat messages. Implementations must be safe for concurrent use.
/// Messages are Microsoft.Extensions.AI <see cref="ChatMessage"/>s; serialize with <c>AIJsonUtilities.DefaultOptions</c>
/// so tool-call/result content round-trips.
/// </summary>
[Instrument("thalos")]
public interface IAgentSessionStore
{
    [Trace("thalos.session.create")]
    ValueTask<Result<AgentSessionRecord, AgentError>> CreateAsync(AgentId agentId, string ownerId, CancellationToken ct);

    [Trace("thalos.session.get")]
    ValueTask<Result<AgentSessionRecord, AgentError>> GetAsync(SessionId id, CancellationToken ct);

    [Trace("thalos.session.list")]
    ValueTask<Result<IReadOnlyList<AgentSessionRecord>, AgentError>> ListAsync(string ownerId, int skip, int take, CancellationToken ct);

    [Trace("thalos.session.messages.load")]
    ValueTask<Result<IReadOnlyList<ChatMessage>, AgentError>> LoadMessagesAsync(SessionId id, CancellationToken ct);

    /// <summary>Append messages in order. Called by the chat-history provider after every model round-trip.</summary>
    [Trace("thalos.session.messages.append")]
    ValueTask<UnitResult<AgentError>> AppendMessagesAsync(SessionId id, IReadOnlyList<ChatMessage> messages, CancellationToken ct);

    /// <summary>Increment TurnCount and token totals, bump LastActivityAt.</summary>
    [Trace("thalos.session.turn.record")]
    ValueTask<UnitResult<AgentError>> RecordTurnAsync(SessionId id, TurnUsage usage, CancellationToken ct);

    [Trace("thalos.session.state.update")]
    ValueTask<UnitResult<AgentError>> UpdateStateAsync(SessionId id, SessionState state, CancellationToken ct);
}
