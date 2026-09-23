namespace Thalos.Workflow;

/// <summary>
/// Everything <see cref="IWorkflowStore.StartAsync(WorkflowStartRequest,CancellationToken)"/> needs to start a
/// run: the positional arguments the original <c>StartAsync</c> overload took, plus the optional
/// <see cref="Manifest"/> that pins the run's agents, skills and documents at creation. Introduced so a caller
/// that wants pinning has somewhere to put it without <c>StartAsync</c> growing a seventh positional parameter —
/// the legacy positional overload becomes a default interface method that builds one of these and forwards, with
/// <see cref="Manifest"/> left <see langword="null"/>.
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

    /// <summary>
    /// The run's write-once pin, or <see langword="null"/> to start a run with no manifest — the same shape a run
    /// started before manifests existed, or through the legacy positional overload, ends up with.
    /// </summary>
    public RunManifest? Manifest { get; init; }
}
