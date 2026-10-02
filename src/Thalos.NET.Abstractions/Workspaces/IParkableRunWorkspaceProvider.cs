using ZeroAlloc.Results;

namespace Thalos.Workspaces;

/// <summary>A workspace provider whose runs can be parked before publish.</summary>
/// <remarks>
/// <b>A park is final (ruling R39).</b> Once a run is parked, its workspace stays parked until publish: nothing unparks
/// it or makes it again, and every run tool the parked workspace served, such as a sandboxed run's <c>workspace__*</c>,
/// <c>sandbox__*</c> and remote <c>runScoped</c> tools, answers that the run has no sandbox. So a process that runs an
/// agent node after a park, behind an await gate or along a reject edge that loops back to an earlier node, is not
/// supported by a provider that parks, such as Thalos.NET.Sandbox's. Unparking or re-creating a parked workspace is
/// future work.
/// </remarks>
public interface IParkableRunWorkspaceProvider
{
    /// <summary>
    /// Stops everything the run's workspace runs, keeping what publish needs. Idempotent. A caller with only
    /// <paramref name="budget"/> left, such as a sweep, also cancels <paramref name="ct"/> when it runs out; a provider
    /// whose park would need longer, for instance to start a stopped workspace again, does not begin it and fails
    /// instead, leaving the park for a later call.
    /// </summary>
    /// <param name="runId">The run.</param>
    /// <param name="budget">How long the caller can wait; <see cref="Timeout.InfiniteTimeSpan"/> for as long as it takes.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<UnitResult<AgentError>> ParkAsync(Guid runId, TimeSpan budget, CancellationToken ct);
}
