namespace Thalos.Workflow;

/// <summary>
/// A running or parked instance of a <see cref="ProcessDefinition"/>: where it is, how many times it has visited
/// each node, and whether it is waiting on an external signal.
/// </summary>
/// <remarks>
/// Declared as a <see langword="record"/>, unlike <see cref="ProcessDefinition"/> and <see cref="ProcessNode"/> —
/// deliberately, and not for free. The interpreter's test suite needs <c>with</c> to build a run positioned at a
/// node with a specific <see cref="Visits"/> count for the <c>maxVisits</c> cap test, and a plain sealed class
/// has no <c>with</c> support. The same caveat that ruled
/// out records for <see cref="ProcessDefinition"/> and <see cref="ProcessNode"/> still applies here: the
/// compiler-generated <see cref="object.Equals(object?)"/> would compare two runs' <see cref="Visits"/>
/// dictionaries by reference, not by content, so it could report two runs with identical visit counts as
/// unequal. Nothing in this task, or its tests, compares two <see cref="WorkflowRun"/> instances for equality —
/// every assertion reads a specific property off one instance — so that caveat costs nothing here in practice.
/// </remarks>
public sealed record WorkflowRun
{
    /// <summary>The run's identity.</summary>
    public required Guid Id { get; init; }

    /// <summary>The name of the process this run is executing.</summary>
    public required string Process { get; init; }

    /// <summary>The version of the process this run is executing.</summary>
    public required int ProcessVersion { get; init; }

    /// <summary>The node the run is currently positioned at.</summary>
    public required string CurrentNode { get; init; }

    /// <summary>The sequence number of the current node's execution, used for optimistic concurrency by Task 4's store.</summary>
    public required long CurrentSeq { get; init; }

    /// <summary>The run's lifecycle status.</summary>
    public required WorkflowStatus Status { get; init; }

    /// <summary>
    /// The signal a parked run is waiting on, or <see langword="null"/> when <see cref="Status"/> is not
    /// <see cref="WorkflowStatus.Awaiting"/>.
    /// </summary>
    public string? AwaitingSignal { get; init; }

    /// <summary>
    /// How many times the run has entered each node so far, keyed by node name; a node absent from this
    /// dictionary has never been entered. Incremented when the run moves onto a node — the start node counts as
    /// one as of <see cref="IWorkflowStore.StartAsync"/> — not when it finishes running. This is a count of
    /// visits, not of completions: <see cref="WorkflowInterpreter.Advance"/> reads it to enforce a node's
    /// <see cref="ProcessNode.MaxVisits"/> cap, comparing <c>Visits[target] + 1</c> — where <c>target</c> is the
    /// node an outgoing edge resolves to, not the node reporting the result — against the cap before that entry
    /// is taken.
    /// </summary>
    public required IReadOnlyDictionary<string, int> Visits { get; init; }

    /// <summary>
    /// Variables accumulated across the run's lifetime — later writes win on key collision, and a write that
    /// carries no variables leaves this bag untouched rather than clearing it. <see cref="NodeResult.Variables"/>
    /// merges into this bag on every <see cref="IWorkflowStore.CompleteNodeAsync"/> and
    /// <see cref="IWorkflowStore.ResumeAsync"/> call, in the same transaction as the rest of that call's writes.
    /// </summary>
    /// <remarks>
    /// <b>What actually writes to this bag today.</b> Exactly one thing:
    /// <see cref="IWorkflowStore.ResumeAsync"/>'s <c>payload</c>, which lands under the literal key
    /// <c>"payload"</c>. <see cref="WorkflowNodeDispatcher"/> builds every <see cref="NodeResult"/> with an empty
    /// variable bag — it reads an agent turn's outcome and nothing else — so a node's own output does not reach
    /// this dictionary through any shipped code path. Node-produced variables are a contract this type and the
    /// store honour (the merge is implemented, transactional and tested) and the dispatcher does not yet
    /// populate: a consumer supplying its own dispatch loop can pass a populated <see cref="NodeResult"/> and the
    /// merge will do the right thing, but out of the box no node's result feeds a later node's input.
    /// </remarks>
    public IReadOnlyDictionary<string, object?> Variables { get; init; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>The most recent error recorded against this run, or <see langword="null"/> if it has not failed.</summary>
    public string? LastError { get; init; }
}
