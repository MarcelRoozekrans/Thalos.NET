namespace Thalos.Workflow;

/// <summary>
/// Everything <see cref="IWorkflowStore.StartAsync(WorkflowStartRequest,CancellationToken)"/> needs to start a
/// run, including the optional <see cref="Manifest"/> that pins the run's agents, skills and documents at
/// creation.
/// </summary>
public sealed record WorkflowStartRequest
{
    /// <summary>The process name to run.</summary>
    public required string Process { get; init; }

    /// <summary>The process version to pin this run to.</summary>
    public required int Version { get; init; }

    /// <summary>The globally unique idempotency key — see <see cref="IWorkflowStore.StartAsync(WorkflowStartRequest,CancellationToken)"/>'s remarks.</summary>
    public required string CorrelationKey { get; init; }

    /// <summary>The node the run begins at.</summary>
    public required string StartNode { get; init; }

    /// <summary>The run's opening <see cref="WorkflowRun.Variables"/> bag, or <see langword="null"/> for a run that starts with nothing.</summary>
    public IReadOnlyDictionary<string, object?>? InitialVariables { get; init; }

    /// <summary>The run's write-once pin, or <see langword="null"/> to start a run with no manifest.</summary>
    public RunManifest? Manifest { get; init; }

    /// <summary>Who started the run. A host with no human behind the start passes its own system principal.</summary>
    public required RunPrincipal StartedBy { get; init; }

    /// <summary>
    /// The id the run is created with. <see langword="null"/> = the store generates one: a supported
    /// configuration for a host that prepares nothing under the run's id before its first dispatch.
    /// </summary>
    public Guid? RunId { get; init; }
}
