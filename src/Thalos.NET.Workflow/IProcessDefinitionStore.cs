using ZeroAlloc.Results;

namespace Thalos.Workflow;

/// <summary>
/// Persists process definitions with version pinning. Declared in this task, implemented in
/// <c>Thalos.Workflow.Orm.OrmProcessDefinitionStore</c> and driven by <see cref="ProcessDefinitionSync"/>, which
/// is the only writer — a definition reaches this store after <see cref="ProcessValidator.ValidateAsync"/> has
/// already accepted it, never before.
/// </summary>
/// <remarks>
/// The property this interface exists to make enforceable: <see cref="WorkflowRun.ProcessVersion"/> is captured
/// once, at <c>IWorkflowStore.StartAsync</c>, and never changes for the life of the run — so a version must stay
/// resolvable for as long as any run that is not yet terminal still pins it, and activating a new version must
/// never disturb the exact shape a live run began on. <see cref="TryRemoveAsync"/> is where that pin is checked;
/// <see cref="UpsertAndActivateAsync"/> is where a new version becomes current without touching any other row.
/// </remarks>
public interface IProcessDefinitionStore
{
    /// <summary>
    /// Stores <paramref name="yaml"/> under <paramref name="definition"/>'s (Name, Version) and makes that
    /// version the one active version for <paramref name="definition"/>'s process, atomically deactivating every
    /// other version of the same process — a version a run is currently pinned to is never removed by this,
    /// only marked no longer the one new runs start on. Called only after
    /// <see cref="ProcessValidator.ValidateAsync"/> has already accepted <paramref name="definition"/>; this
    /// method does not validate and never rejects a definition on its own account.
    /// </summary>
    ValueTask UpsertAndActivateAsync(ProcessDefinition definition, string yaml, CancellationToken ct);

    /// <summary>The version currently active for <paramref name="process"/>, or <see langword="null"/> if none has ever activated.</summary>
    ValueTask<int?> GetActiveVersionAsync(string process, CancellationToken ct);

    /// <summary>
    /// Reads the definition stored for <paramref name="process"/> version <paramref name="version"/> — the
    /// read-by-version path run-time resolution takes. A run pins <see cref="WorkflowRun.ProcessVersion"/> once,
    /// at <c>IWorkflowStore.StartAsync</c>, and never changes it, so this exact pair — not
    /// <see cref="GetActiveVersionAsync"/>'s answer — is what a live run must keep resolving against: activating
    /// a newer version must never silently move a run that started on an older one onto a different graph.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="Result{T}.Failure"/>, never <see langword="null"/> and never a throw, for both ways a
    /// version can fail to resolve — no row stored for the pair, and a stored row whose YAML no longer parses —
    /// with the process and version named in the message either way, because the caller's only sensible response
    /// to either is to fail the run with something a human can act on. An exception escaping this method means
    /// the backing store itself was unreachable, which is a transient infrastructure failure a retry can fix and
    /// is deliberately a different channel from "this definition does not resolve".
    /// </remarks>
    ValueTask<Result<ProcessDefinition>> GetAsync(string process, int version, CancellationToken ct);

    /// <summary>
    /// Removes <paramref name="process"/> version <paramref name="version"/> — but not when a run that has not
    /// yet reached a terminal status still pins it (see this interface's remarks), which surfaces as
    /// <see cref="Result.Failure"/> rather than a thrown exception. A <paramref name="version"/> with no stored
    /// row — never activated, or already removed — is a no-op success: nothing pins it, so there is nothing to
    /// refuse.
    /// </summary>
    ValueTask<Result> TryRemoveAsync(string process, int version, CancellationToken ct);
}
