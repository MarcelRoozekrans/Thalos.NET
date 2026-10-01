using ZeroAlloc.Results;

namespace Thalos.Workspaces;

/// <summary>Hands a run's changes to host code for publishing.</summary>
public interface IRunWorkspaceHandoff
{
    /// <summary>A workspace whose Root is a host directory holding the run's changes, ready for host code to commit and push. Idempotent.</summary>
    ValueTask<Result<RunWorkspace, AgentError>> CheckoutForPublishAsync(Guid runId, CancellationToken ct);
}
