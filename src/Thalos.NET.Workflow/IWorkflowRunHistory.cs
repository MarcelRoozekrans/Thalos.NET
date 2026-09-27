namespace Thalos.Workflow;

/// <summary>
/// Reads a run's event log back: what <see cref="IWorkflowStore"/> recorded at each start, completion, resume,
/// failure and cancellation, including each completed node's token usage. Read-only, and separate from
/// <see cref="IWorkflowStore"/> so a host that decorates the store's writes does not also have to forward reads.
/// </summary>
public interface IWorkflowRunHistory
{
    /// <summary>
    /// Every event recorded for <paramref name="runId"/>, in ascending <see cref="WorkflowRunEvent.Seq"/> order.
    /// Empty for a run id that has no events, including one that does not exist.
    /// </summary>
    ValueTask<IReadOnlyList<WorkflowRunEvent>> ListEventsAsync(Guid runId, CancellationToken ct);
}
