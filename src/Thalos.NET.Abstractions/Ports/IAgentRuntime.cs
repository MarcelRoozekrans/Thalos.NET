using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos;

/// <summary>Front door of the framework. Channels (HTTP, CLI, Telegram…) only ever talk to this.</summary>
public interface IAgentRuntime
{
    /// <summary>
    /// Creates an <see cref="SessionState.Idle"/> session for <paramref name="agentId"/> owned by <paramref name="caller"/>
    /// (<c>ISecurityContext.Id</c>) and publishes <see cref="SessionCreatedNotification"/>. Unknown agent →
    /// <see cref="AgentErrorCode.AgentNotFound"/>; store failure → <see cref="AgentErrorCode.StoreError"/>.
    /// </summary>
    ValueTask<Result<SessionId, AgentError>> CreateSessionAsync(AgentId agentId, ISecurityContext caller, CancellationToken ct = default);

    /// <summary>Runs one turn and returns the buffered result.</summary>
    ValueTask<Result<AgentTurnResult, AgentError>> RunTurnAsync(AgentTurnRequest request, CancellationToken ct = default);

    /// <summary>Runs one turn, streaming <see cref="AgentEvent"/>s. Ends with <see cref="TurnCompletedEvent"/> or <see cref="TurnFailedEvent"/>.</summary>
    IAsyncEnumerable<AgentEvent> RunTurnStreamingAsync(AgentTurnRequest request, CancellationToken ct = default);

    /// <summary>
    /// Closes the session (→ <see cref="SessionState.Closed"/>, terminal) and publishes <see cref="SessionClosedNotification"/>.
    /// Only the owner or an admin may close it (otherwise <see cref="AgentErrorCode.SessionNotFound"/>); a session that is
    /// running a turn → <see cref="AgentErrorCode.SessionBusy"/>; already closed → <see cref="AgentErrorCode.SessionClosed"/>.
    /// </summary>
    ValueTask<UnitResult<AgentError>> CloseSessionAsync(SessionId sessionId, ISecurityContext caller, CancellationToken ct = default);
}
