namespace Thalos.Workflow;

/// <summary>
/// Schedules the agent/skill work behind a node once a <see cref="WorkflowTransition"/> moves a run onto it.
/// Declared in this task, implemented in Task 4 as an outbox-backed dispatcher whose enqueue happens inside the
/// same transaction as <see cref="IWorkflowStore.CompleteNodeAsync"/> — so a run never records a transition
/// without the work behind its new node also being scheduled, and never schedules that work without the
/// transition having committed.
/// </summary>
/// <remarks>
/// The brief for this task specifies <see cref="IWorkflowStore"/>'s exact surface but not this interface's; the
/// single method below is this task's best-effort placeholder for "enqueue the node dispatch that Task 4's
/// transactional outbox will pick up," sized to what Task 4's brief describes needing. Task 4 owns the final
/// shape and should widen or reshape this member as its outbox payload turns out to require.
/// </remarks>
public interface IWorkflowDispatcher
{
    /// <summary>Schedules <paramref name="node"/> of run <paramref name="runId"/> to run its agent/skill pair.</summary>
    ValueTask DispatchAsync(Guid runId, string node, CancellationToken ct);
}
