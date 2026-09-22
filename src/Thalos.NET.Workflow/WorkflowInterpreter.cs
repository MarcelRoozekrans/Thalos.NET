using ZeroAlloc.Results;

namespace Thalos.Workflow;

/// <summary>
/// The pure decision layer of the workflow engine: given a process, the run currently positioned at one of its
/// nodes, and the result that node just produced, decides the run's next transition. Performs no I/O and
/// touches no database — every fact it needs is already in its three parameters — which is what makes it
/// exhaustively testable in memory and is the property <see cref="IWorkflowStore"/> and
/// <see cref="IWorkflowDispatcher"/> implementations build on.
/// </summary>
/// <remarks>
/// Evaluates a node in a fixed order — cap, then gate, then branch, then <c>next</c>, then terminal — because a
/// node's shape can combine more of these fields than <see cref="ProcessValidator"/> forbids in combination (a
/// gate can also carry <c>maxVisits</c>, for instance), and only one order gives a predictable answer when it
/// does:
/// <list type="number">
/// <item>
/// <b>Cap.</b> Skipped unless <see cref="ProcessNode.MaxVisits"/> is set. Compares
/// <c>Visits[node] + 1 &gt; MaxVisits</c> — the ordinal of the completion just reported, not the count that will
/// exist after it — before any outgoing edge is considered, so a <c>maxVisits: 5</c> node's sixth completion,
/// and only its sixth, is redirected to <see cref="ProcessNode.OnExceeded"/> instead of its usual edge. A cap
/// with no <c>OnExceeded</c> target now fails <see cref="ProcessValidator"/> at load time, so the null check
/// below is defence in depth: unreachable for any process that validated, kept in case a caller constructs a
/// <see cref="ProcessDefinition"/> by hand without going through the validator.
/// </item>
/// <item>
/// <b>Gate.</b> <see cref="ProcessNode.Await"/> set means the run parks at this node, awaiting an external
/// signal, regardless of any <c>branch</c>/<c>next</c> also present on it.
/// </item>
/// <item>
/// <b>Branch.</b> A non-empty <see cref="ProcessNode.Branch"/> requires <see cref="NodeResult.Outcome"/> to be
/// one of the node's declared <see cref="ProcessNode.Outcomes"/>. <see cref="ProcessValidator"/> guarantees
/// every declared branch key is a declared outcome, but it cannot know at load time what an agent will actually
/// report at run time — this is the one guard <c>Advance</c> supplies that validation cannot, and the reason an
/// unconstrained model reply can never silently pick a branch.
/// </item>
/// <item><b>Next.</b> An unconditional successor, for a node with no branch.</item>
/// <item><b>Terminal.</b> The node ends the run at its declared status.</item>
/// </list>
/// </remarks>
public static class WorkflowInterpreter
{
    /// <summary>
    /// Decides the transition out of <paramref name="run"/>'s current node given <paramref name="result"/>.
    /// Performs no I/O. Fails rather than transition when <paramref name="result"/>'s outcome is not one of the
    /// node's declared outcomes.
    /// </summary>
    public static Result<WorkflowTransition> Advance(ProcessDefinition process, WorkflowRun run, NodeResult result)
    {
        var node = process.Nodes[run.CurrentNode];

        if (node.MaxVisits is int maxVisits)
        {
            var completionOrdinal = run.Visits.GetValueOrDefault(run.CurrentNode) + 1;
            if (completionOrdinal > maxVisits)
            {
                // Defence in depth: ProcessValidator now rejects maxVisits without onExceeded at load time,
                // so this branch is unreachable through the validated path. Kept for a hand-built
                // ProcessDefinition that bypassed the validator.
                if (node.OnExceeded is null)
                {
                    return Result<WorkflowTransition>.Failure(
                        $"node '{run.CurrentNode}' exceeded its maxVisits cap of {maxVisits} but declares no onExceeded target");
                }

                return Result<WorkflowTransition>.Success(
                    new WorkflowTransition(node.OnExceeded, WorkflowStatus.Running, null, WorkflowEventKind.CapExceeded));
            }
        }

        if (node.Await is not null)
        {
            return Result<WorkflowTransition>.Success(
                new WorkflowTransition(run.CurrentNode, WorkflowStatus.Awaiting, node.Await, WorkflowEventKind.Awaiting));
        }

        if (node.Branch.Count > 0)
        {
            return AdvanceBranch(node, run.CurrentNode, result.Outcome);
        }

        if (node.Next is not null)
        {
            return Result<WorkflowTransition>.Success(
                new WorkflowTransition(node.Next, WorkflowStatus.Running, null, WorkflowEventKind.Completed));
        }

        if (node.Terminal is not null)
        {
            return AdvanceTerminal(node.Terminal, run.CurrentNode);
        }

        return Result<WorkflowTransition>.Failure(
            $"node '{run.CurrentNode}' has no outgoing edge and is not terminal — this should have been caught at load time");
    }

    private static Result<WorkflowTransition> AdvanceBranch(ProcessNode node, string nodeName, string? outcome)
    {
        if (outcome is null || !node.Outcomes.Contains(outcome, StringComparer.Ordinal))
        {
            return Result<WorkflowTransition>.Failure(
                $"node '{nodeName}' produced outcome '{outcome}' which is not one of its declared outcomes ({string.Join(", ", node.Outcomes)})");
        }

        if (!node.Branch.TryGetValue(outcome, out var branchTarget))
        {
            return Result<WorkflowTransition>.Failure(
                $"node '{nodeName}' has no branch destination for outcome '{outcome}'");
        }

        return Result<WorkflowTransition>.Success(
            new WorkflowTransition(branchTarget, WorkflowStatus.Running, null, WorkflowEventKind.Branched));
    }

    private static Result<WorkflowTransition> AdvanceTerminal(string terminal, string nodeName)
    {
        if (!Enum.TryParse<WorkflowStatus>(terminal, ignoreCase: true, out var terminalStatus))
        {
            return Result<WorkflowTransition>.Failure(
                $"node '{nodeName}' has an unrecognized terminal status '{terminal}'");
        }

        return Result<WorkflowTransition>.Success(
            new WorkflowTransition(nodeName, terminalStatus, null, WorkflowEventKind.Completed));
    }
}
