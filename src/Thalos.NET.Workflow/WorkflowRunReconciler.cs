namespace Thalos.Workflow;

/// <summary>
/// Terminates runs stranded by a dead-lettered dispatch message: <see cref="WorkflowStatus.Running"/>, with
/// nothing in flight and nothing that will ever advance them. The outbox delivers at-least-once and
/// dead-letters after its configured retry budget; when that happens the run it was carrying sits at
/// <see cref="WorkflowStatus.Running"/> forever, looking alive while nothing will ever move it again. A plain
/// class over <see cref="IWorkflowStore"/> — this ships no timer of its own. Hosting <see cref="SweepAsync"/> on
/// a schedule is the consumer's job, the same way <see cref="WorkflowNodeDispatcher"/> leaves dispatch
/// invocation to its host: a framework that starts its own timers fights whatever hosting model the consumer
/// already has.
/// </summary>
/// <remarks>
/// <para>
/// <b>Terminates; never advances.</b> A tempting alternative shape for this class would "recover" a stranded
/// run by re-driving it through <see cref="WorkflowInterpreter.Advance"/> — re-dispatching its current node, or
/// nudging it forward. That is deliberately not what this does. <see cref="IWorkflowStore.ResumeAsync"/>'s
/// contract requires calling <c>Advance</c> with the run's persisted status still <see cref="WorkflowStatus.Awaiting"/>,
/// because that is the only signal <c>Advance</c> has to distinguish a genuine resume from a fresh arrival at
/// the same gate — a store (or a caller) that reaches <c>Advance</c> any other way with a stale
/// <see cref="WorkflowStatus.Awaiting"/> run resumes the gate rather than re-parking it. Since this sweep never
/// calls <c>Advance</c> at all, that hazard cannot arise here: every run it acts on is unconditionally failed
/// via <see cref="IWorkflowStore.FailAsync"/>, which records the termination and stops, without evaluating a
/// single edge of the process graph. A caller that wants the work redone starts a fresh run; nothing about
/// "stranded" implies "recoverable in place."
/// </para>
/// <para>
/// <b>Awaiting runs are never candidates.</b> <see cref="IWorkflowStore.FindStrandedAsync"/> itself already
/// excludes <see cref="WorkflowStatus.Awaiting"/> runs — see that member's remarks — so this sweep never even
/// considers a run parked at an approval gate, no matter how long it has sat there. That is not an oversight:
/// a gate has nothing in flight by design, and terminating it for staleness would destroy exactly the
/// human-in-the-loop work the gate exists to protect.
/// </para>
/// </remarks>
public sealed class WorkflowRunReconciler(IWorkflowStore store)
{
    private readonly IWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>
    /// Finds every run <see cref="IWorkflowStore.FindStrandedAsync"/> reports stranded for at least
    /// <paramref name="olderThan"/> and fails each one with a named reason, via
    /// <see cref="IWorkflowStore.FailAsync"/> — which is already idempotent on a run that reached a terminal
    /// state some other way between the query and this call. Returns the number of runs terminated, so a
    /// caller's periodic host can log a non-zero sweep the way <c>Daedalus.Agents.Scheduling.ScheduleSweeperService</c>
    /// logs a non-zero claim count.
    /// </summary>
    /// <param name="olderThan">
    /// How long a run must have gone without progress to count as stranded. Must comfortably exceed the
    /// longest duration a healthy node's agent turn is expected to run — an agent turn legitimately takes
    /// minutes, and a threshold anywhere near that risks failing perfectly healthy work still in flight. The
    /// value is entirely caller-supplied: this type hardcodes no default, the same way it hosts no timer of its
    /// own — both are the consumer's call to make for its own workload.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of runs this sweep terminated.</returns>
    public async ValueTask<int> SweepAsync(TimeSpan olderThan, CancellationToken ct)
    {
        var stranded = await _store.FindStrandedAsync(olderThan, ct).ConfigureAwait(false);

        var terminated = 0;
        foreach (var run in stranded)
        {
            await _store.FailAsync(
                run.Id,
                $"stranded: run '{run.Id}' made no progress for at least {olderThan} while Running — its dispatch " +
                "message most likely dead-lettered after exhausting the outbox's retry budget, leaving nothing " +
                "left to advance it.",
                ct).ConfigureAwait(false);
            terminated++;
        }

        return terminated;
    }
}
