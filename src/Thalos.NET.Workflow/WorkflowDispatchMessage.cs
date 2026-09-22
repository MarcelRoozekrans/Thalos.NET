namespace Thalos.Workflow;

/// <summary>
/// The outbox message a backend <see cref="IWorkflowStore"/> implementation enqueues, in the same transaction as
/// the run's transition, whenever that transition leaves the run at <see cref="WorkflowStatus.Running"/>. Public,
/// and living in the dependency-free core package, so both a store implementation (e.g.
/// <c>Thalos.Workflow.Orm.OrmWorkflowStore</c>) and a node dispatcher (<see cref="WorkflowNodeDispatcher"/>) bind
/// to one real, versioned type instead of each hand-writing a matching DTO and re-typing
/// <see cref="WorkflowDispatch.TypeName"/> as a magic string on their own side of a process boundary where a
/// rename would break silently with no compile error. Originally declared in the <c>.Orm</c> package; moved here
/// because it is pure data with no backend dependency, and core is the right home for a contract both sides bind
/// to — unlike a dispatch <em>interface</em>, which would need a transaction parameter to be implementable and
/// was deliberately kept out of core for exactly that reason.
/// </summary>
/// <param name="RunId">The run the dispatched node belongs to.</param>
/// <param name="Seq">
/// The run's sequence number as of this dispatch. Travels with the message so a redelivery whose <c>Seq</c> no
/// longer matches the run's current sequence number can be dropped as a no-op rather than acted on twice.
/// </param>
/// <param name="Node">The node to run the agent/skill pair for.</param>
public sealed record WorkflowDispatchMessage(Guid RunId, long Seq, string Node);

/// <summary>Outbox message type names a workflow store enqueues under.</summary>
public static class WorkflowDispatch
{
    /// <summary>The <c>typeName</c> a <see cref="WorkflowDispatchMessage"/> is enqueued and dequeued under.</summary>
    public const string TypeName = "thalos.workflow.node-dispatch";
}
