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
    /// <paramref name="initialVariables"/> seeds <see cref="WorkflowRun.Variables"/> so the run starts holding
    /// the work item it exists to perform.
    /// </summary>
    /// <param name="process">The process name to run.</param>
    /// <param name="version">The process version to pin this run to.</param>
    /// <param name="correlationKey">The globally unique idempotency key — see this method's remarks.</param>
    /// <param name="startNode">The node the run begins at.</param>
    /// <param name="initialVariables">
    /// The run's opening <see cref="WorkflowRun.Variables"/> bag — the issue, branch, diff or whatever else the
    /// first node needs to act on — or <see langword="null"/> for a run that starts with nothing. Null and an
    /// empty dictionary are the same thing here: both leave <see cref="WorkflowRun.Variables"/> an <em>empty</em>
    /// dictionary, never <see langword="null"/>, so a caller reading it back never has to null-check. A required
    /// parameter rather than a defaulted one deliberately: an omitted-by-default seed on the one method that can
    /// give a run its work item is exactly the silent no-op this signature change exists to rule out. Seeded only
    /// on the path that actually starts a run: a call whose <paramref name="correlationKey"/> an earlier run
    /// already used starts nothing and therefore seeds nothing, leaving that earlier run's own bag as it stands.
    /// <para>
    /// Bounded: an implementation throws <see cref="ArgumentException"/> for a bag holding more keys than a run
    /// may carry. The cap is what lets the engine name every variable it has to leave out of a node's task text,
    /// so a store that accepted an unbounded seed would silently weaken that guarantee for every run it started.
    /// </para>
    /// </param>
    /// <param name="ct">Cancels the start.</param>
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
    /// <para>
    /// <b>Superseded by <see cref="StartAsync(WorkflowStartRequest,CancellationToken)"/>.</b> This overload is a
    /// default interface method that forwards to it with <see cref="WorkflowStartRequest.Manifest"/> left
    /// <see langword="null"/> — a run started this way carries no pin, the same as any run started before
    /// manifests existed. An implementation only needs to provide the request-based overload; it never needs to
    /// implement this one itself. The blank-argument guards below run against this overload's own parameter
    /// names before <see cref="WorkflowStartRequest"/> is built, so a caller of this overload — including one
    /// reaching it purely through this default implementation, with no override of its own — sees
    /// <c>ArgumentException.ParamName</c> "process", "correlationKey" or "startNode", not "request".
    /// </para>
    /// </remarks>
    ValueTask<Guid> StartAsync(
        string process,
        int version,
        string correlationKey,
        string startNode,
        IReadOnlyDictionary<string, object?>? initialVariables,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(process);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(startNode);

        return StartAsync(
            new WorkflowStartRequest
            {
                Process = process,
                Version = version,
                CorrelationKey = correlationKey,
                StartNode = startNode,
                InitialVariables = initialVariables,
            },
            ct);
    }

    /// <summary>
    /// Starts a new run exactly as <see cref="StartAsync(string,int,string,string,IReadOnlyDictionary{string,object?}?,CancellationToken)"/>
    /// does, from the fields of <paramref name="request"/>, and additionally writes
    /// <see cref="WorkflowStartRequest.Manifest"/> onto the created run as <see cref="WorkflowRun.Manifest"/> —
    /// once, at the same <c>INSERT</c> that creates the row. Nothing after that ever updates it: not
    /// <see cref="CompleteNodeAsync"/>, not <see cref="ResumeAsync"/>, not any other member of this interface. A
    /// run found through the idempotent path — <see cref="WorkflowStartRequest.CorrelationKey"/> already in use —
    /// keeps whatever manifest its original call gave it; <paramref name="request"/>'s own
    /// <see cref="WorkflowStartRequest.Manifest"/> is discarded along with the rest of that no-op start, the same
    /// way its <see cref="WorkflowStartRequest.InitialVariables"/> already is.
    /// </summary>
    /// <param name="request">The run to start.</param>
    /// <param name="ct">Cancels the start.</param>
    ValueTask<Guid> StartAsync(WorkflowStartRequest request, CancellationToken ct);

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
