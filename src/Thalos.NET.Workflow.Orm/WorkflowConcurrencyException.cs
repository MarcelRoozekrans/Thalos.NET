namespace Thalos.Workflow.Orm;

/// <summary>
/// Thrown by <see cref="OrmWorkflowStore"/> when an <c>UPDATE</c> against <c>workflow_run</c> affects zero rows:
/// the row's <c>xmin</c> read at the start of the operation no longer matches the row in the database, because
/// another writer committed a change to the same run first, or the caller's <c>seq</c> is stale. Either way the
/// caller lost the race — its write did not land — and must re-read the run rather than assume it did.
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
