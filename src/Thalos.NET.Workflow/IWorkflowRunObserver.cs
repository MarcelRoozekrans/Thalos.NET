namespace Thalos.Workflow;

/// <summary>
///     Told about every change the workflow store commits to a run, after the commit, and never about a change the
///     store refused. Use it to notify a console; never to change the run, and never to block: the store awaits each
///     observer in turn on the caller's path. An observer that throws is logged by exception type and ignored, because
///     the change is already committed.
/// </summary>
public interface IWorkflowRunObserver
{
    /// <summary>Called once per committed change.</summary>
    ValueTask OnChangedAsync(WorkflowRunChange change, CancellationToken ct);
}

/// <summary>What changed. Carries no variables, outcome, error or payload, so it can be broadcast as it is.</summary>
/// <param name="RunId">The run that changed.</param>
/// <param name="Kind">Which store operation committed the change.</param>
/// <param name="Status">The run's status after the change.</param>
/// <param name="CurrentNode">The node the run is at after the change.</param>
/// <param name="AwaitingSignal">The signal the run waits on after the change, or <see langword="null"/>.</param>
public sealed record WorkflowRunChange(
    Guid RunId, WorkflowRunChangeKind Kind, WorkflowStatus Status, string CurrentNode, string? AwaitingSignal);

/// <summary>The store operation that committed a <see cref="WorkflowRunChange"/>.</summary>
public enum WorkflowRunChangeKind
{
    /// <summary><see cref="IWorkflowStore.StartAsync"/> created the run.</summary>
    Started,

    /// <summary><see cref="IWorkflowStore.CompleteNodeAsync"/> moved the run on.</summary>
    NodeCompleted,

    /// <summary><see cref="IWorkflowStore.ResumeAsync"/> moved a parked run on.</summary>
    Resumed,

    /// <summary><see cref="IWorkflowStore.FailAsync"/> or <see cref="IWorkflowStore.FailStrandedAsync"/> failed the run.</summary>
    Failed,

    /// <summary><see cref="IWorkflowStore.CancelAsync"/> cancelled the run.</summary>
    Cancelled,

    /// <summary><see cref="IWorkflowStore.RetryFailedNodeAsync"/> re-queued a failed node.</summary>
    Retried,
}
