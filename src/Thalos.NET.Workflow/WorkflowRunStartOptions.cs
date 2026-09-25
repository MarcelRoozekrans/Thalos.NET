namespace Thalos.Workflow;

/// <summary>
/// Everything <see cref="WorkflowRunStarter.StartAsync"/> needs to resolve a process's active version, pin its
/// manifest and start the run — the only start member <see cref="WorkflowRunStarter"/> exposes.
/// </summary>
public sealed record WorkflowRunStartOptions
{
    /// <summary>The process name to run.</summary>
    public required string Process { get; init; }

    /// <summary>The globally unique idempotency key — see <see cref="IWorkflowStore.StartAsync(WorkflowStartRequest,CancellationToken)"/>'s remarks.</summary>
    public required string CorrelationKey { get; init; }

    /// <summary><see langword="null"/> = the run starts with no variables.</summary>
    public IReadOnlyDictionary<string, object?>? Variables { get; init; }

    /// <summary><see langword="null"/> = the manifest pins no documents.</summary>
    public IReadOnlyDictionary<string, string>? Documents { get; init; }

    /// <summary>Who started the run. A host with no human behind the start passes its own system principal.</summary>
    public required RunPrincipal StartedBy { get; init; }

    /// <summary><see langword="null"/> = the store generates the id, as on <see cref="WorkflowStartRequest.RunId"/>.</summary>
    public Guid? RunId { get; init; }
}
