namespace Thalos.Workflow;

/// <summary>The lifecycle state of a <see cref="WorkflowRun"/>.</summary>
public enum WorkflowStatus
{
    /// <summary>The run is actively progressing through the graph.</summary>
    Running,

    /// <summary>The run is parked at a gate, waiting for an external signal.</summary>
    Awaiting,

    /// <summary>The run reached a terminal node whose declared status is success.</summary>
    Succeeded,

    /// <summary>The run reached a terminal node whose declared status is failure, or failed outright.</summary>
    Failed,

    /// <summary>The run was cancelled before reaching a terminal node.</summary>
    Cancelled,
}
