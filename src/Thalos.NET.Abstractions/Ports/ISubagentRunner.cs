using ZeroAlloc.Results;

namespace Thalos;

/// <summary>
/// Runs one agent turn with <em>no live caller</em> — nobody is holding a socket open for the answer. Used by hosts
/// for scheduled runs and for orchestrated subagent steps; both are the same thing triggered differently.
/// </summary>
/// <remarks>
/// Never streams. A live turn streams because someone is watching it arrive; a detached run has no such audience, so
/// it returns the buffered <see cref="AgentTurnResult"/> and the host decides how to deliver it — typically through a
/// transactional outbox, so a crash between "the agent decided what to say" and "the channel sent it" cannot drop it.
/// </remarks>
public interface ISubagentRunner
{
    /// <summary>
    /// Creates a fresh session for <c>request.AgentId</c> owned by <c>request.Caller</c>, runs exactly one turn with
    /// <c>request.Task</c>, closes the session, and returns the result. Unknown agent →
    /// <see cref="AgentErrorCode.AgentNotFound"/>; over budget → <see cref="AgentErrorCode.SubagentBudgetExceeded"/>;
    /// past the deadline → <see cref="AgentErrorCode.SubagentDeadlineExceeded"/>; too deeply nested →
    /// <see cref="AgentErrorCode.SubagentDepthExceeded"/>. The session is closed on every path, including failure.
    /// </summary>
    ValueTask<Result<AgentTurnResult, AgentError>> RunAsync(SubagentRunRequest request, CancellationToken ct = default);
}
