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
/// Evaluates a node in a fixed order — gate, then resolve the outgoing edge via <c>branch</c> or <c>next</c>,
/// then cap-check the resolved target, then terminal:
/// <list type="number">
/// <item>
/// <b>Gate.</b> <see cref="ProcessNode.Await"/> set means the run parks at this node, awaiting an external
/// signal, regardless of any <c>branch</c>/<c>next</c> also present on it. Checked first because a park is a
/// decision about the current node itself, not an edge to resolve — there is no target yet to cap-check.
/// </item>
/// <item>
/// <b>Resolve the edge.</b> A non-empty <see cref="ProcessNode.Branch"/> requires <see cref="NodeResult.Outcome"/>
/// to be one of the node's declared <see cref="ProcessNode.Outcomes"/>, then looks up that outcome's branch
/// target. <see cref="ProcessValidator"/> guarantees every declared branch key is a declared outcome, but it
/// cannot know at load time what an agent will actually report at run time — this is the one guard
/// <c>Advance</c> supplies that validation cannot, and the reason an unconstrained model reply can never
/// silently pick a branch. A node with no branch instead takes its unconditional <c>next</c>.
/// </item>
/// <item>
/// <b>Cap-check the resolved target, not the node reporting the result.</b> <c>maxVisits</c> bounds how many
/// times the node carrying it may be entered — so the cap that matters belongs to whichever node the edge just
/// resolved to, not to the node whose completion produced <c>result</c>. Checking the source node
/// instead, as an earlier version of this method did, breaks a working outcome: a capped node that finishes
/// successfully on its last allowed run and branches to a different, uncapped node would be redirected to its
/// own <c>onExceeded</c> anyway, discarding a good result purely because of when it arrived. Checking the
/// target means an edge that leaves a capped node is never intercepted by that node's own cap — only an edge
/// that re-enters a capped node, directly or by looping back around through other nodes, is. Compares
/// <c>Visits[target] + 1 &gt; MaxVisits</c> — <see cref="WorkflowRun.Visits"/> counts entries, not completions,
/// so <c>Visits[target] + 1</c> is the ordinal the entry about to happen would carry — before that entry is
/// taken, so a <c>maxVisits: 5</c> node's fifth completion is the last one to ever run: a sixth entry is never
/// reached, redirected instead to the target's own <see cref="ProcessNode.OnExceeded"/>. A cap with no
/// <c>OnExceeded</c> target now fails <see cref="ProcessValidator"/> at load time, so the null check below is
/// defence in depth: unreachable for any process that validated, kept in case a caller constructs a
/// <see cref="ProcessDefinition"/> by hand without going through the validator.
/// </item>
/// <item>
/// <b>Terminal.</b> Reached only when the current node has neither <c>branch</c> nor <c>next</c> to resolve —
/// it ends the run at its declared status instead of naming a target to cap-check.
/// <see cref="ProcessValidator"/> restricts <c>terminal</c> to <c>succeeded</c> or <c>failed</c> at load time,
/// so the <c>Enum.TryParse</c> guard below is defence in depth in the same sense as the cap's <c>OnExceeded</c>
/// check: unreachable for any process that validated, kept for a hand-built <see cref="ProcessDefinition"/>.
/// </item>
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

        if (node.Await is not null)
        {
            return Result<WorkflowTransition>.Success(
                new WorkflowTransition(run.CurrentNode, WorkflowStatus.Awaiting, node.Await, WorkflowEventKind.Awaiting));
        }

        string target;
        WorkflowEventKind kind;

        if (node.Branch.Count > 0)
        {
            var branchTarget = ResolveBranch(node, run.CurrentNode, result.Outcome);
            if (branchTarget.IsFailure)
            {
                return Result<WorkflowTransition>.Failure(branchTarget.Error);
            }

            target = branchTarget.Value;
            kind = WorkflowEventKind.Branched;
        }
        else if (node.Next is not null)
        {
            target = node.Next;
            kind = WorkflowEventKind.Completed;
        }
        else if (node.Terminal is not null)
        {
            return AdvanceTerminal(node.Terminal, run.CurrentNode);
        }
        else
        {
            return Result<WorkflowTransition>.Failure(
                $"node '{run.CurrentNode}' has no outgoing edge and is not terminal — this should have been caught at load time");
        }

        return ApplyCap(process, run, target, kind);
    }

    private static Result<string> ResolveBranch(ProcessNode node, string nodeName, string? outcome)
    {
        if (outcome is null || !node.Outcomes.Contains(outcome, StringComparer.Ordinal))
        {
            return Result<string>.Failure(
                $"node '{nodeName}' produced outcome '{outcome}' which is not one of its declared outcomes ({string.Join(", ", node.Outcomes)})");
        }

        if (!node.Branch.TryGetValue(outcome, out var branchTarget))
        {
            return Result<string>.Failure(
                $"node '{nodeName}' has no branch destination for outcome '{outcome}'");
        }

        return Result<string>.Success(branchTarget);
    }

    private static Result<WorkflowTransition> ApplyCap(ProcessDefinition process, WorkflowRun run, string target, WorkflowEventKind kind)
    {
        var targetNode = process.Nodes[target];

        if (targetNode.MaxVisits is int maxVisits)
        {
            var entryOrdinal = run.Visits.GetValueOrDefault(target) + 1;
            if (entryOrdinal > maxVisits)
            {
                // Defence in depth: ProcessValidator now rejects maxVisits without onExceeded at load time,
                // so this branch is unreachable through the validated path. Kept for a hand-built
                // ProcessDefinition that bypassed the validator.
                if (targetNode.OnExceeded is null)
                {
                    return Result<WorkflowTransition>.Failure(
                        $"node '{target}' exceeded its maxVisits cap of {maxVisits} but declares no onExceeded target");
                }

                return Result<WorkflowTransition>.Success(
                    new WorkflowTransition(targetNode.OnExceeded, WorkflowStatus.Running, null, WorkflowEventKind.CapExceeded));
            }
        }

        return Result<WorkflowTransition>.Success(new WorkflowTransition(target, WorkflowStatus.Running, null, kind));
    }

    private static Result<WorkflowTransition> AdvanceTerminal(string terminal, string nodeName)
    {
        // Defence in depth: ProcessValidator now restricts terminal to succeeded/failed at load time,
        // so this branch is unreachable through the validated path. Kept for a hand-built
        // ProcessDefinition that bypassed the validator.
        if (!Enum.TryParse<WorkflowStatus>(terminal, ignoreCase: true, out var terminalStatus))
        {
            return Result<WorkflowTransition>.Failure(
                $"node '{nodeName}' has an unrecognized terminal status '{terminal}'");
        }

        return Result<WorkflowTransition>.Success(
            new WorkflowTransition(nodeName, terminalStatus, null, WorkflowEventKind.Completed));
    }
}
