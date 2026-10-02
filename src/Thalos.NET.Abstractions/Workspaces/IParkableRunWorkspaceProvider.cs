using ZeroAlloc.Results;

namespace Thalos.Workspaces;

/// <summary>A workspace provider whose runs can be parked before publish.</summary>
public interface IParkableRunWorkspaceProvider
{
    /// <summary>Stops everything the run's workspace runs, keeping what publish needs. Idempotent.</summary>
    ValueTask<UnitResult<AgentError>> ParkAsync(Guid runId, CancellationToken ct);

    /// <summary>
    /// <see cref="ParkAsync(Guid, CancellationToken)"/> for a caller with only <paramref name="budget"/> left, such as a
    /// sweep: a provider whose park would need longer, for instance to start a stopped workspace again, does not begin
    /// it and fails instead, leaving the park for a later call. By default the budget is ignored.
    /// </summary>
    /// <param name="runId">The run.</param>
    /// <param name="budget">How long the caller can wait; the caller also cancels <paramref name="ct"/> when it runs out.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<UnitResult<AgentError>> ParkAsync(Guid runId, TimeSpan budget, CancellationToken ct) => ParkAsync(runId, ct);
}
