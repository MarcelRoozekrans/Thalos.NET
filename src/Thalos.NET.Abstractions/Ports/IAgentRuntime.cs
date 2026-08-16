using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos;

/// <summary>Front door of the framework. Channels (HTTP, CLI, Telegram…) only ever talk to this.</summary>
public interface IAgentRuntime
{
    ValueTask<Result<SessionId, AgentError>> CreateSessionAsync(AgentId agentId, ISecurityContext caller, CancellationToken ct = default);

    /// <summary>Runs one turn and returns the buffered result.</summary>
    ValueTask<Result<AgentTurnResult, AgentError>> RunTurnAsync(AgentTurnRequest request, CancellationToken ct = default);

    /// <summary>Runs one turn, streaming <see cref="AgentEvent"/>s. Ends with <see cref="TurnCompletedEvent"/> or <see cref="TurnFailedEvent"/>.</summary>
    IAsyncEnumerable<AgentEvent> RunTurnStreamingAsync(AgentTurnRequest request, CancellationToken ct = default);

    ValueTask<UnitResult<AgentError>> CloseSessionAsync(SessionId sessionId, ISecurityContext caller, CancellationToken ct = default);
}
