namespace Thalos.Workflow;

/// <summary>
/// The result of running the node a <see cref="WorkflowRun"/> is currently positioned at, handed to
/// <see cref="WorkflowInterpreter.Advance"/> to decide the next transition.
/// </summary>
public sealed class NodeResult
{
    /// <summary>Creates a result carrying <paramref name="outcome"/> and <paramref name="variables"/>.</summary>
    public NodeResult(string? outcome, IReadOnlyDictionary<string, object?> variables)
    {
        Outcome = outcome;
        Variables = variables;
    }

    /// <summary>
    /// The outcome the node's agent reported, or <see langword="null"/> for a node with no declared
    /// <c>outcomes</c> (a plain sequence node, or a gate — neither ever produces one). When the node this result
    /// belongs to declares a non-empty <c>branch</c>, <see cref="WorkflowInterpreter.Advance"/> requires this to
    /// be one of that node's declared outcomes, and fails rather than guess when it is not.
    /// </summary>
    public string? Outcome { get; }

    /// <summary>
    /// Variables the node produced, to be merged into the run's state by the caller.
    /// <see cref="IWorkflowStore.CompleteNodeAsync"/> merges them into <see cref="WorkflowRun.Variables"/>;
    /// an empty dictionary merges nothing and clears nothing.
    /// </summary>
    /// <remarks>
    /// On the shipped dispatch path <see cref="WorkflowNodeDispatcher"/> populates this from the variables
    /// argument of the node's outcome tool call — the same single call the outcome itself is read from, never
    /// from the turn's text. A node with no declared <c>outcomes</c> is offered no outcome tool and so always
    /// produces an empty bag here: it has no read path to report through, which is correct for a node that
    /// declares nothing. A consumer driving its own dispatch loop can of course supply values directly.
    /// </remarks>
    public IReadOnlyDictionary<string, object?> Variables { get; }
}
