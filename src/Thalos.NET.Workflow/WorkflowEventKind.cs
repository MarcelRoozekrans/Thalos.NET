namespace Thalos.Workflow;

/// <summary>
/// What kind of event a <see cref="WorkflowTransition"/> represents. <see cref="WorkflowInterpreter.Advance"/>
/// produces <see cref="Completed"/>, <see cref="Branched"/>, <see cref="Awaiting"/> and
/// <see cref="CapExceeded"/> — the four decisions its evaluation order can reach. <see cref="Entered"/>,
/// <see cref="Resumed"/> and <see cref="Failed"/> are recorded by <see cref="IWorkflowStore"/> implementations
/// around a run's start, resume and failure, which are not <c>Advance</c>'s concern: <c>Advance</c> decides what
/// happens after a node produces a result, not how a run began or how a parked run wakes back up.
/// </summary>
public enum WorkflowEventKind
{
    /// <summary>A run started, or moved onto a node for the first time.</summary>
    Entered,

    /// <summary>A node ran to completion and its unconditional <c>next</c> edge was taken.</summary>
    Completed,

    /// <summary>A node's declared outcome selected a <c>branch</c> edge.</summary>
    Branched,

    /// <summary>A run parked at a gate, waiting for an external signal.</summary>
    Awaiting,

    /// <summary>A parked run resumed after its awaited signal arrived.</summary>
    Resumed,

    /// <summary>The run failed outright, outside the normal node/outcome flow.</summary>
    Failed,

    /// <summary>A loop-back node's <c>maxVisits</c> cap was reached and its <c>onExceeded</c> edge was taken instead.</summary>
    CapExceeded,
}
