namespace Thalos.Workflow;

/// <summary>Asks <see cref="IWorkflowStore.RetryFailedNodeAsync"/> to re-run a failed run's host-action node.</summary>
public sealed record WorkflowRetryRequest
{
    /// <summary>
    /// The <see cref="WorkflowRun.CurrentSeq"/> the caller read. A run that moved on since, including one another
    /// retry already restarted, is refused, so two operators cannot both re-run the action.
    /// </summary>
    public required long ExpectedSeq { get; init; }

    /// <summary>Who asked for the retry. Recorded on the <see cref="WorkflowEventKind.Retried"/> event as <see cref="WorkflowRunEvent.Actor"/>.</summary>
    public required RunPrincipal RetriedBy { get; init; }
}
