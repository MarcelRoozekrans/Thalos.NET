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
    /// returns its id. Seeds <see cref="WorkflowRun.Visits"/> with <paramref name="startNode"/> already counted
    /// as one entry — the run has entered it by virtue of starting there — so a start node that also carries a
    /// <c>maxVisits</c> cap is bounded correctly from its very first run, not given one free, uncounted entry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A started run is a dispatched run.</b> An implementation must schedule <paramref name="startNode"/>'s
    /// own execution as part of this call, in the same unit of work that writes the run: a caller does not — and
    /// must not have to — mint the first <see cref="WorkflowDispatchMessage"/> itself. An implementation that
    /// only writes the row leaves every run it creates at <see cref="WorkflowStatus.Running"/> with nothing that
    /// will ever advance it, and <see cref="WorkflowRunReconciler.SweepAsync"/> will eventually terminate each
    /// one as stranded.
    /// </para>
    /// <para>
    /// <b><paramref name="correlationKey"/> is unique across every process and for all time.</b> The key space
    /// is global: it is not scoped per <paramref name="process"/>, and it is not released when a run reaches a
    /// terminal status. A second call with a key some earlier run already used returns <em>that</em> run's id —
    /// whatever process and version it belonged to, and whether it is still running, succeeded, failed or
    /// cancelled months ago — and starts nothing. A caller that wants a fresh run must supply a key nothing has
    /// ever used, so keys are worth minting with the attempt in them (a run id, a timestamp, an attempt counter)
    /// rather than from a business identity that recurs.
    /// </para>
    /// </remarks>
    ValueTask<Guid> StartAsync(string process, int version, string correlationKey, string startNode, CancellationToken ct);

    /// <summary>Finds a run by id, or <see langword="null"/> if none exists.</summary>
    ValueTask<WorkflowRun?> FindAsync(Guid runId, CancellationToken ct);

    /// <summary>
    /// Records that the node at <paramref name="seq"/> produced <paramref name="result"/> and applies
    /// <paramref name="transition"/> atomically with appending to the run's event log: the run moves to
    /// <see cref="WorkflowTransition.NextNode"/> at <see cref="WorkflowTransition.NextStatus"/>. When
    /// <see cref="WorkflowTransition.NextNode"/> differs from the run's current node, <see cref="WorkflowRun.Visits"/>'s
    /// count for it is incremented by one — <c>Visits</c> counts entries, so the increment lands on the node the
    /// run is entering, not the one it just finished. When <see cref="WorkflowTransition.NextNode"/> is instead
    /// the <em>same</em> node the run is already at — a gate parking on itself while awaiting a signal, or a
    /// terminal node ending the run — no entry has occurred and <c>Visits</c> must not be incremented; doing so
    /// would burn a visit per park against any capped gate purely from staying put.
    /// <see cref="WorkflowInterpreter.Advance"/> computed <paramref name="transition"/> assuming this is the
    /// increment that happens; a store that increments a different node's count, increments the same node
    /// twice, or increments on a self-transition, breaks the cap check on <paramref name="transition"/>'s next
    /// call.
    /// </summary>
    ValueTask CompleteNodeAsync(Guid runId, long seq, WorkflowTransition transition, NodeResult result, CancellationToken ct);

    /// <summary>
    /// Resumes a run parked at a gate awaiting <paramref name="signal"/>, carrying <paramref name="payload"/>
    /// into the run's variables under the literal key <c>"payload"</c> — a store that merges
    /// <paramref name="payload"/> in some other shape binds a different, undocumented contract than a caller
    /// reading <see cref="WorkflowRun.Variables"/>["payload"] expects.
    /// </summary>
    /// <remarks>
    /// This method does not decide where the run goes next — it verifies <paramref name="signal"/> matches
    /// <see cref="WorkflowRun.AwaitingSignal"/>, then defers entirely to
    /// <see cref="WorkflowInterpreter.Advance"/> for that decision, the same way <see cref="CompleteNodeAsync"/>
    /// does for a task node's completion. Critically, it must call <c>Advance</c> with the run's
    /// <see cref="WorkflowRun.Status"/> still <see cref="WorkflowStatus.Awaiting"/> — that is the only signal
    /// <c>Advance</c> has to tell a resume from a fresh arrival at the same gate, and calling it with
    /// <see cref="WorkflowStatus.Running"/> already set would make it park all over again. <c>Advance</c>
    /// returns a <see cref="WorkflowTransition"/> whose <see cref="WorkflowTransition.NextStatus"/> is what
    /// actually flips the run to <see cref="WorkflowStatus.Running"/> (or further, if a cap redirects it) —
    /// applied by this method exactly as <see cref="CompleteNodeAsync"/> applies one. A store that computes the
    /// gate's successor itself, instead of calling <c>Advance</c>, duplicates the interpreter's edge logic in a
    /// second place, and the two will drift. Every failure mode — the run not found, the signal not matching,
    /// the process unregistered, or an optimistic-concurrency loss applying the transition — surfaces as
    /// <see cref="Result.Failure"/>, not a thrown exception: a caller that only matches on this method's
    /// <see cref="Result"/> should never need a second, exception-based error channel to also handle.
    /// </remarks>
    ValueTask<Result> ResumeAsync(Guid runId, string signal, string? payload, CancellationToken ct);

    /// <summary>Marks a run failed with <paramref name="errorMessage"/>, outside the normal node/outcome flow.</summary>
    ValueTask FailAsync(Guid runId, string errorMessage, CancellationToken ct);

    /// <summary>
    /// The seq-guarded counterpart to <see cref="FailAsync"/> that <see cref="WorkflowRunReconciler.SweepAsync"/>
    /// uses instead of it. A run <see cref="FindStrandedAsync"/> reported at <paramref name="expectedSeq"/> can
    /// legitimately move on — completed by a concurrent dispatcher, including into
    /// <see cref="WorkflowStatus.Awaiting"/> at a gate — at any point between that query's snapshot and this
    /// call actually reaching the row, and every such transition bumps <see cref="WorkflowRun.CurrentSeq"/>
    /// (<see cref="CompleteNodeAsync"/> and <see cref="ResumeAsync"/> alike, unconditionally). This method fails
    /// the run only if it is still at <paramref name="expectedSeq"/> at the moment of the write; otherwise it is
    /// a no-op, not a throw and not a fight over the row — the run moved on to something this sweep has no
    /// business overwriting, most importantly a gate it must never destroy. Also a no-op, like
    /// <see cref="FailAsync"/>, when the run already reached a terminal state some other way. Returns whether
    /// this call actually failed the run.
    /// </summary>
    ValueTask<bool> FailStrandedAsync(Guid runId, long expectedSeq, string errorMessage, CancellationToken ct);

    /// <summary>Cancels a run for <paramref name="reason"/> before it reaches a terminal node.</summary>
    ValueTask CancelAsync(Guid runId, string reason, CancellationToken ct);

    /// <summary>
    /// Finds runs stranded by a dead-lettered dispatch message, for <see cref="WorkflowRunReconciler"/> to
    /// terminate. Only <see cref="WorkflowStatus.Running"/> runs are candidates: a run
    /// <see cref="WorkflowStatus.Awaiting"/> a signal has nothing in flight by design (it is parked at an
    /// approval gate, not stranded) and may legitimately sit there for days, so it is never returned here no
    /// matter how stale <see cref="WorkflowRun.CurrentSeq"/>'s last update is. Results are ordered oldest-updated
    /// first and capped at an implementation-defined batch size, so a sweep run always terminates in bounded
    /// time even when many runs qualify — a run left over past the cap is picked up by the next sweep, since a
    /// stranded run stays stranded until something terminates it.
    /// </summary>
    ValueTask<IReadOnlyList<WorkflowRun>> FindStrandedAsync(TimeSpan olderThan, CancellationToken ct);
}
