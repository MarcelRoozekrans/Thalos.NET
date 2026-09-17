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
/// <remarks>
/// <b>A deadline stops work; a budget settles it.</b> The deadline is a stop signal aimed at work still in flight —
/// once it fires, the linked token is cancelled and nothing further is spent chasing the turn. The token budget is
/// evaluated after the turn returns, against tokens already spent, and settles whether that already-completed work is
/// within what was allowed. The two therefore do not always agree: a turn that races past its deadline and still
/// comes back with a real result is reported as a <em>success</em> — the deadline had nothing left to stop, and the
/// tokens it spent were already spent — but the runner logs a warning so that "succeeded, arrived late" is at least
/// observable instead of silently indistinguishable from an on-time success.
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
