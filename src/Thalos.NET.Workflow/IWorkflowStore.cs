using ZeroAlloc.Results;

namespace Thalos.Workflow;

/// <summary>
/// Persists <see cref="WorkflowRun"/> state and the append-only event log behind it. Declared in this task,
/// implemented in Task 4 as an ORM-backed store, and consumed by Tasks 8 through 10 for dispatch, resume
/// webhooks and stranded-run recovery. <see cref="WorkflowInterpreter.Advance"/> never calls any member here —
/// it is pure and knows nothing about persistence, which is exactly what makes it exhaustively testable in
/// memory.
/// </summary>
public interface IWorkflowStore
{
    /// <summary>
    /// Starts a new run of <paramref name="process"/> version <paramref name="version"/> at
    /// <paramref name="startNode"/>, keyed for idempotent lookup by <paramref name="correlationKey"/>, and
    /// returns its id.
    /// </summary>
    ValueTask<Guid> StartAsync(string process, int version, string correlationKey, string startNode, CancellationToken ct);

    /// <summary>Finds a run by id, or <see langword="null"/> if none exists.</summary>
    ValueTask<WorkflowRun?> FindAsync(Guid runId, CancellationToken ct);

    /// <summary>
    /// Records that the node at <paramref name="seq"/> produced <paramref name="result"/> and applies
    /// <paramref name="transition"/> — the run's new node, status and visit count — atomically with appending to
    /// the run's event log.
    /// </summary>
    ValueTask CompleteNodeAsync(Guid runId, long seq, WorkflowTransition transition, NodeResult result, CancellationToken ct);

    /// <summary>
    /// Resumes a run parked at a gate awaiting <paramref name="signal"/>, carrying <paramref name="payload"/>
    /// into the run's variables.
    /// </summary>
    ValueTask<Result> ResumeAsync(Guid runId, string signal, string? payload, CancellationToken ct);

    /// <summary>Marks a run failed with <paramref name="errorMessage"/>, outside the normal node/outcome flow.</summary>
    ValueTask FailAsync(Guid runId, string errorMessage, CancellationToken ct);

    /// <summary>Cancels a run for <paramref name="reason"/> before it reaches a terminal node.</summary>
    ValueTask CancelAsync(Guid runId, string reason, CancellationToken ct);

    /// <summary>Finds runs that have not progressed in at least <paramref name="olderThan"/>, for stranded-run recovery.</summary>
    ValueTask<IReadOnlyList<WorkflowRun>> FindStrandedAsync(TimeSpan olderThan, CancellationToken ct);
}
