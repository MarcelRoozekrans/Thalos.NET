using ZeroAlloc.Results;

namespace Thalos.Workflow;

/// <summary>
/// Validates a <see cref="ProcessDefinition"/> at load time, before a run can spend agent turns on a graph that
/// was always going to fail partway through. All rules below are checked and every failure is collected, so a
/// single malformed file is reported in full rather than one fault at a time.
/// </summary>
/// <remarks>
/// Reachability ("can I get here from the start?") and terminal-path ("can I get from here to an end?") are
/// answered by two separate graph walks — <see cref="ValidateReachability"/> and
/// <see cref="ValidateTerminalPaths"/> — deliberately, rather than one combined pass. They are different
/// questions over different directions of the same edge set, and folding them together is exactly where an
/// unreachable-node bug likes to hide.
/// </remarks>
public static class ProcessValidator
{
    /// <summary>
    /// Validates <paramref name="process"/>'s shape — every reference resolves, every node is reachable from
    /// <see cref="ProcessDefinition.StartNode"/>, every node can reach a terminal, every node declaring
    /// <c>maxVisits</c> also declares <c>onExceeded</c> and vice versa without naming itself, no <c>onExceeded</c>
    /// redirect can route back into the node it just capped, every declared <c>terminal</c> is <c>succeeded</c>
    /// or <c>failed</c>, no declared <c>outcome</c> is blank or repeated, every node is exactly one of task
    /// (<c>agent</c> and <c>skill</c> both present), gate or terminal, a gate (<c>await</c> set) resolves via
    /// <c>next</c> only — never <c>branch</c>/<c>outcomes</c> — a terminal declares no outgoing edge at all, and,
    /// when <paramref name="resolver"/> is not <see langword="null"/>, that every declared agent and skill exists
    /// in the host.
    /// </summary>
    public static async ValueTask<Result<ProcessDefinition>> ValidateAsync(
        ProcessDefinition process, IWorkflowReferenceResolver? resolver, CancellationToken ct)
    {
        var errors = new List<string>();

        ValidateShape(process, errors);
        ValidateReachability(process, errors);
        ValidateTerminalPaths(process, errors);

        if (resolver is not null)
        {
            await ValidateReferencesAsync(process, resolver, errors, ct).ConfigureAwait(false);
        }

        return errors.Count == 0
            ? Result<ProcessDefinition>.Success(process)
            : Result<ProcessDefinition>.Failure(string.Join("; ", errors));
    }

    /// <summary>
    /// The per-node shape rules that need no graph walk: every <c>next</c>/<c>branch</c> value/<c>onExceeded</c>
    /// target names a node that exists; every <c>branch</c> key is a declared outcome; a node declaring
    /// <c>branch</c> also declares <c>outcomes</c>; <c>agent</c> and <c>skill</c> are both present or both
    /// absent — a node cannot run an agent's default instructions with the skill unpinned;
    /// <see cref="ValidateCapAndTerminal"/>'s <c>maxVisits</c>/<c>onExceeded</c>/<c>terminal</c> rules;
    /// <see cref="ValidateCapRedirectEscapes"/>'s check that a cap's redirect cannot route back into the capped
    /// node; <see cref="ValidateOutcomeSet"/>'s blank/duplicate <c>outcomes</c> rules; and a node is exactly one
    /// of task, gate or terminal.
    /// </summary>
    private static void ValidateShape(ProcessDefinition process, List<string> errors)
    {
        foreach (var (name, node) in process.Nodes)
        {
            foreach (var target in OutgoingTargets(node))
            {
                if (!process.Nodes.ContainsKey(target))
                {
                    errors.Add($"node '{name}' references unknown node '{target}'");
                }
            }

            foreach (var key in node.Branch.Keys)
            {
                if (!node.Outcomes.Contains(key, StringComparer.Ordinal))
                {
                    errors.Add($"node '{name}' branch key '{key}' is not a declared outcome");
                }
            }

            if (node.Branch.Count > 0 && node.Outcomes.Count == 0)
            {
                errors.Add($"node '{name}' declares 'branch' without declaring 'outcomes'");
            }

            ValidateCapAndTerminal(name, node, errors);
            ValidateCapRedirectEscapes(process, name, node, errors);
            ValidateOutcomeSet(name, node, errors);

            if (node.Agent is not null && node.Skill is null)
            {
                errors.Add($"node '{name}' has 'agent' but no 'skill'");
            }

            if (node.Skill is not null && node.Agent is null)
            {
                errors.Add($"node '{name}' has 'skill' but no 'agent'");
            }

            var isTask = node.Agent is not null && node.Skill is not null;
            var isGate = node.Await is not null;
            var isTerminal = node.Terminal is not null;
            var kindCount = (isTask ? 1 : 0) + (isGate ? 1 : 0) + (isTerminal ? 1 : 0);
            if (kindCount != 1)
            {
                errors.Add($"node '{name}' must be exactly one of task, gate or terminal");
            }

            // A gate resolves via 'next' only, checked on Outcomes alone — not Branch too. A gate with 'branch'
            // and no 'outcomes' is already caught above by "declares 'branch' without declaring 'outcomes'"; a
            // gate with 'branch' AND 'outcomes' is caught by the Outcomes.Count > 0 check right here. No input
            // exists where a Branch.Count > 0 disjunct would be the one that flips this verdict, so it is left
            // out rather than kept as a guard that cannot fail by construction. Without this check at all, a
            // gate could still declare 'outcomes' and validate cleanly, but the interpreter's resume path has no
            // declared outcome to branch on (a signal's payload is not one of the node's Outcomes), leaving the
            // signal-to-branch mapping an unstated convention. Forbidding the shape is simpler than inventing it.
            if (isGate && node.Outcomes.Count > 0)
            {
                errors.Add($"node '{name}' is a gate ('await' set) and must resolve via 'next' only — 'branch'/'outcomes' are not allowed on a gate");
            }
        }
    }

    /// <summary>
    /// The <c>maxVisits</c>/<c>onExceeded</c> pairing and self-reference rules, plus the <c>terminal</c> status
    /// rule — split out of <see cref="ValidateShape"/> to keep that method under the analyzer's line limit.
    /// <c>maxVisits</c> and <c>onExceeded</c> are both present or both absent — a cap with nowhere to route to,
    /// or a route with no cap behind it, would only be discovered after a run had already paid for the agent
    /// turns that hit it; <c>onExceeded</c> never names its own node — redirecting a cap back to the node it
    /// just capped would spin forever, exactly the loop <c>maxVisits</c> exists to bound; and a declared
    /// <c>terminal</c> is <c>succeeded</c> or <c>failed</c>, compared case-insensitively to match
    /// <see cref="WorkflowInterpreter"/>'s own parsing — <c>cancelled</c> is an operator action through
    /// <c>CancelAsync</c>, not a destination a process graph gets to declare, and any other value would
    /// otherwise validate cleanly and only fail once a run reached it.
    /// </summary>
    private static void ValidateCapAndTerminal(string name, ProcessNode node, List<string> errors)
    {
        if (node.MaxVisits is not null && node.OnExceeded is null)
        {
            errors.Add($"node '{name}' declares 'maxVisits' but no 'onExceeded' target");
        }

        if (node.OnExceeded is not null && node.MaxVisits is null)
        {
            errors.Add($"node '{name}' declares 'onExceeded' but no 'maxVisits' cap");
        }

        if (node.OnExceeded is not null && string.Equals(node.OnExceeded, name, StringComparison.Ordinal))
        {
            errors.Add($"node '{name}' declares 'onExceeded: {name}' — a cap cannot redirect to the node it just capped, or it would spin forever");
        }

        if (node.Terminal is not null &&
            !node.Terminal.Equals("succeeded", StringComparison.OrdinalIgnoreCase) &&
            !node.Terminal.Equals("failed", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"node '{name}' has an unrecognized terminal status '{node.Terminal}' (must be 'succeeded' or 'failed'; 'cancelled' is an operator action, not a declared destination)");
        }

        // The mirror of the gate rule above, and for the same reason. WorkflowInterpreter.Advance evaluates a
        // node in a fixed order — gate, then branch, then next, then terminal — so a terminal node that also
        // carries an outgoing edge has that edge taken and its terminal status never reached. The run walks on
        // past the node that was supposed to end it. Forbidding the shape is the only way that combination is
        // ever reported; nothing downstream can tell an author their end state is unreachable.
        if (node.Terminal is not null && (node.Next is not null || node.Branch.Count > 0 || node.Outcomes.Count > 0))
        {
            errors.Add($"node '{name}' is a terminal ('terminal' set) and must not also declare 'next'/'branch'/'outcomes' — the outgoing edge is resolved first, so the run would take it and never reach the terminal status");
        }
    }

    /// <summary>
    /// The cap's redirect must actually escape the loop it caps. <see cref="WorkflowInterpreter.Advance"/> hands
    /// back <see cref="ProcessNode.OnExceeded"/> without cap-checking that redirect target's own onward edges, so
    /// <c>a: { maxVisits: 5, onExceeded: b }</c> paired with <c>b: { next: a }</c> runs <c>a</c> five times,
    /// redirects to <c>b</c>, and is routed straight back into <c>a</c> — where the cap fires again, forever,
    /// paying for <c>b</c>'s turn on every cycle. <c>maxVisits</c> is the only bound this engine puts on what a
    /// run can spend, so a redirect that re-enters the capped node defeats the one guarantee it offers. Checked
    /// here, at load time, by forward-walking the graph from the redirect: if the capped node is reachable again,
    /// the process is rejected before a run ever starts.
    /// </summary>
    /// <remarks>
    /// <see cref="ValidateCapAndTerminal"/>'s self-reference rule already reports <c>onExceeded</c> naming its own
    /// node, so that case is skipped here rather than reported a second time under different wording. An
    /// <c>onExceeded</c> naming a node that does not exist is skipped too — <see cref="ValidateShape"/>'s
    /// unknown-target rule owns that one, and there is no graph to walk from a node the process has not got.
    /// The walk follows <see cref="ValidOutgoingTargets"/>, which includes other nodes' own <c>onExceeded</c>
    /// edges: a cap that redirects into a second cap that redirects back is just as non-terminating as a plain
    /// <c>next</c> that loops round, and both should be caught.
    /// </remarks>
    private static void ValidateCapRedirectEscapes(ProcessDefinition process, string name, ProcessNode node, List<string> errors)
    {
        if (node.MaxVisits is null ||
            node.OnExceeded is not { } redirect ||
            string.Equals(redirect, name, StringComparison.Ordinal) ||
            !process.Nodes.ContainsKey(redirect))
        {
            return;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal) { redirect };
        var queue = new Queue<string>();
        queue.Enqueue(redirect);

        while (queue.Count > 0)
        {
            foreach (var target in ValidOutgoingTargets(process, process.Nodes[queue.Dequeue()]))
            {
                if (string.Equals(target, name, StringComparison.Ordinal))
                {
                    errors.Add(
                        $"node '{name}' declares 'onExceeded: {redirect}', but '{name}' is reachable again from '{redirect}' — the cap would redirect and be routed straight back into '{name}', so the loop never terminates");
                    return;
                }

                if (visited.Add(target))
                {
                    queue.Enqueue(target);
                }
            }
        }
    }

    /// <summary>
    /// A node's declared <c>outcomes</c> become the closed <c>enum</c> of the outcome tool
    /// <c>WorkflowNodeDispatcher</c> offers the agent, and <c>OutcomeTool.Validate</c> refuses a set containing a
    /// blank value or a duplicate. Without these two rules here, <c>outcomes: [approved, rejected, rejected]</c>
    /// loads and validates cleanly and then fails on <em>every single dispatch</em> of that node — the shape this
    /// validator exists to make impossible, since the whole point of load-time validation is that a graph which
    /// passes it does not fail per node once a run is live and spending turns.
    /// </summary>
    private static void ValidateOutcomeSet(string name, ProcessNode node, List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var outcome in node.Outcomes)
        {
            if (string.IsNullOrWhiteSpace(outcome))
            {
                errors.Add($"node '{name}' declares a blank outcome — every declared outcome must be a non-blank value, or the outcome tool built for this node is rejected on every dispatch");
            }
            else if (!seen.Add(outcome))
            {
                errors.Add($"node '{name}' declares outcome '{outcome}' more than once — a duplicate makes the outcome tool's closed set unbuildable, so every dispatch of this node would fail");
            }
        }
    }

    /// <summary>
    /// Traversal 1: forward BFS from <see cref="ProcessDefinition.StartNode"/> following <c>next</c>,
    /// <c>branch</c> and <c>onExceeded</c> edges. Any node never reached this way can never run.
    /// </summary>
    private static void ValidateReachability(ProcessDefinition process, List<string> errors)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        if (process.Nodes.ContainsKey(process.StartNode))
        {
            visited.Add(process.StartNode);
            queue.Enqueue(process.StartNode);
        }

        while (queue.Count > 0)
        {
            var current = process.Nodes[queue.Dequeue()];
            foreach (var target in ValidOutgoingTargets(process, current))
            {
                if (visited.Add(target))
                {
                    queue.Enqueue(target);
                }
            }
        }

        foreach (var name in process.Nodes.Keys)
        {
            if (!visited.Contains(name))
            {
                errors.Add($"unreachable node '{name}'");
            }
        }
    }

    /// <summary>
    /// Traversal 2: reverse BFS starting from every terminal node, walking <c>next</c>/<c>branch</c>/
    /// <c>onExceeded</c> edges backwards. A node reached this way can eventually finish; a node that a cycle
    /// keeps forever, without ever reaching a terminal, cannot.
    /// </summary>
    private static void ValidateTerminalPaths(ProcessDefinition process, List<string> errors)
    {
        var reverseEdges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, node) in process.Nodes)
        {
            foreach (var target in ValidOutgoingTargets(process, node))
            {
                if (!reverseEdges.TryGetValue(target, out var sources))
                {
                    reverseEdges[target] = sources = [];
                }

                sources.Add(name);
            }
        }

        var reaches = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        foreach (var (name, node) in process.Nodes)
        {
            if (node.Terminal is not null && reaches.Add(name))
            {
                queue.Enqueue(name);
            }
        }

        while (queue.Count > 0)
        {
            if (!reverseEdges.TryGetValue(queue.Dequeue(), out var sources))
            {
                continue;
            }

            foreach (var source in sources)
            {
                if (reaches.Add(source))
                {
                    queue.Enqueue(source);
                }
            }
        }

        foreach (var name in process.Nodes.Keys)
        {
            if (!reaches.Contains(name))
            {
                errors.Add($"no path from '{name}' reaches a terminal");
            }
        }
    }

    /// <summary>Resolver-backed rule: every non-null <c>agent</c>/<c>skill</c> must exist in the host.</summary>
    private static async ValueTask ValidateReferencesAsync(
        ProcessDefinition process, IWorkflowReferenceResolver resolver, List<string> errors, CancellationToken ct)
    {
        foreach (var (name, node) in process.Nodes)
        {
            if (node.Agent is not null && await resolver.ResolveAgentIdAsync(node.Agent, ct).ConfigureAwait(false) is null)
            {
                errors.Add($"node '{name}' references unknown agent '{node.Agent}'");
            }

            if (node.Skill is not null && !await resolver.SkillExistsAsync(node.Skill, ct).ConfigureAwait(false))
            {
                errors.Add($"node '{name}' references unknown skill '{node.Skill}'");
            }
        }
    }

    /// <summary>Every node name a node's <c>next</c>/<c>branch</c>/<c>onExceeded</c> fields point at, unfiltered.</summary>
    private static IEnumerable<string> OutgoingTargets(ProcessNode node)
    {
        if (node.Next is not null)
        {
            yield return node.Next;
        }

        foreach (var target in node.Branch.Values)
        {
            yield return target;
        }

        if (node.OnExceeded is not null)
        {
            yield return node.OnExceeded;
        }
    }

    /// <summary>
    /// <see cref="OutgoingTargets"/>, filtered to targets that actually exist. The graph walks must not follow —
    /// or crash on — an edge to a node <see cref="ValidateShape"/> has already flagged as unknown.
    /// </summary>
    private static IEnumerable<string> ValidOutgoingTargets(ProcessDefinition process, ProcessNode node) =>
        OutgoingTargets(node).Where(process.Nodes.ContainsKey);
}
