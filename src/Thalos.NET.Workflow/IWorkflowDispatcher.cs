namespace Thalos.Workflow;

/// <summary>
/// Schedules the agent/skill work behind a node once a <see cref="WorkflowTransition"/> moves a run onto it.
/// Declared in this task, implemented in Task 4 as an outbox-backed dispatcher whose enqueue happens inside the
/// same transaction as <see cref="IWorkflowStore.CompleteNodeAsync"/> — so a run never records a transition
/// without the work behind its new node also being scheduled, and never schedules that work without the
/// transition having committed.
/// </summary>
public interface IWorkflowDispatcher
{
    /// <summary>
    /// Schedules <paramref name="node"/> of run <paramref name="runId"/> to run its agent/skill pair.
    /// <paramref name="seq"/> travels with the dispatched message so the engine's redelivery defence has
    /// something to compare against: a message whose <paramref name="seq"/> no longer matches the run's current
    /// sequence number is dropped as a no-op rather than acted on twice.
    /// </summary>
    ValueTask DispatchAsync(Guid runId, long seq, string node, CancellationToken ct);
}
