namespace Thalos.Workflow.Orm;

/// <summary>
/// The outbox message <see cref="OrmWorkflowStore.CompleteNodeAsync"/> and <see cref="OrmWorkflowStore.ResumeAsync"/>
/// enqueue, in the same transaction as the run's transition, whenever that transition leaves the run at
/// <see cref="WorkflowStatus.Running"/>. Public so Task 8's outbox consumer binds to a real, versioned type
/// instead of hand-writing a matching DTO and re-typing <see cref="WorkflowDispatch.TypeName"/> as a magic
/// string on its side of a process boundary where a rename would break silently with no compile error.
/// </summary>
/// <param name="RunId">The run the dispatched node belongs to.</param>
/// <param name="Seq">
/// The run's sequence number as of this dispatch. Travels with the message so a redelivery whose <c>Seq</c> no
/// longer matches the run's current sequence number can be dropped as a no-op rather than acted on twice.
/// </param>
/// <param name="Node">The node to run the agent/skill pair for.</param>
public sealed record WorkflowDispatchMessage(Guid RunId, long Seq, string Node);

/// <summary>Outbox message type names <c>Thalos.Workflow.Orm</c> enqueues under.</summary>
public static class WorkflowDispatch
{
    /// <summary>The <c>typeName</c> a <see cref="WorkflowDispatchMessage"/> is enqueued and dequeued under.</summary>
    public const string TypeName = "thalos.workflow.node-dispatch";
}
