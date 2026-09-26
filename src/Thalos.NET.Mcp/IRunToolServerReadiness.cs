using ZeroAlloc.Results;

namespace Thalos.Mcp;

/// <summary>Lets a host wait, before it dispatches a run's work, until that run's own MCP servers are ready.</summary>
public interface IRunToolServerReadiness
{
    /// <summary>
    /// Starts a run's servers if they are not running, for example after a host restart, and waits until every one is
    /// ready. Fails naming the server and the timeout. Succeeds immediately when no run-scoped server is configured.
    /// </summary>
    /// <param name="runId">The run whose servers must be ready.</param>
    /// <param name="timeout">How long to wait for every server together.</param>
    /// <param name="ct">Cancellation token; cancelling it stops the wait, not the servers.</param>
    ValueTask<UnitResult<AgentError>> WaitAllReadyAsync(Guid runId, TimeSpan timeout, CancellationToken ct);
}
