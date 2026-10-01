using ZeroAlloc.Results;

namespace Thalos.Workspaces;

/// <summary>A workspace provider whose runs can be parked before publish.</summary>
public interface IParkableRunWorkspaceProvider
{
    /// <summary>Stops everything the run's workspace runs, keeping what publish needs. Idempotent.</summary>
    ValueTask<UnitResult<AgentError>> ParkAsync(Guid runId, CancellationToken ct);
}
