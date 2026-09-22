namespace Thalos.Workflow;

/// <summary>
/// The decision <see cref="WorkflowInterpreter.Advance"/> reaches for a <see cref="WorkflowRun"/>: which node it
/// moves to next, what its status becomes, and — for a gate — the signal it now waits on.
/// </summary>
public sealed class WorkflowTransition
{
    /// <summary>Creates a transition to <paramref name="nextNode"/>.</summary>
    public WorkflowTransition(string nextNode, WorkflowStatus nextStatus, string? awaitingSignal, WorkflowEventKind kind)
    {
        NextNode = nextNode;
        NextStatus = nextStatus;
        AwaitingSignal = awaitingSignal;
        Kind = kind;
    }

    /// <summary>
    /// The node the run is at after this transition: the target of a <c>next</c>, <c>branch</c> or
    /// <c>onExceeded</c> edge, or the same node the run was already at when it parks at a gate or reaches a
    /// terminal.
    /// </summary>
    public string NextNode { get; }

    /// <summary>The run's status after this transition.</summary>
    public WorkflowStatus NextStatus { get; }

    /// <summary>
    /// The signal a parked run now waits on. <see langword="null"/> except when <see cref="NextStatus"/> is
    /// <see cref="WorkflowStatus.Awaiting"/>.
    /// </summary>
    public string? AwaitingSignal { get; }

    /// <summary>What kind of event this transition represents.</summary>
    public WorkflowEventKind Kind { get; }
}
