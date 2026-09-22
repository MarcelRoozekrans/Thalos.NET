namespace Thalos.Workflow;

/// <summary>
/// Thrown by an <see cref="IWorkflowStore"/> implementation when it loses an optimistic-concurrency race: a
/// write against a <see cref="WorkflowRun"/> did not land because the row changed underneath it — another
/// writer committed a change to the same run first, or the caller's sequence number is stale. Either way the
/// caller lost the race and must re-read the run rather than assume its write took effect. Declared in the
/// core package, not a backend-specific one, so a consumer holding only <see cref="IWorkflowStore"/> can catch
/// it without depending on whichever persistence package implements the store.
/// </summary>
public sealed class WorkflowConcurrencyException : Exception
{
    /// <summary>Creates an exception with no message.</summary>
    public WorkflowConcurrencyException()
    {
    }

    /// <summary>Creates an exception carrying <paramref name="message"/>.</summary>
    public WorkflowConcurrencyException(string message) : base(message)
    {
    }

    /// <summary>Creates an exception carrying <paramref name="message"/> and <paramref name="innerException"/>.</summary>
    public WorkflowConcurrencyException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
