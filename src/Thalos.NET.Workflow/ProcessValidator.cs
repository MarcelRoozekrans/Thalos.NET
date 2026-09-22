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
    /// <c>maxVisits</c> also declares <c>onExceeded</c> and vice versa, every declared <c>terminal</c> is
    /// <c>succeeded</c> or <c>failed</c>, and every node is exactly one of task (<c>agent</c> and <c>skill</c>
    /// both present), gate or terminal — and, when <paramref name="resolver"/> is not <see langword="null"/>,
    /// that every declared agent and skill exists in the host.
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
    /// absent — a node cannot run an agent's default instructions with the skill unpinned; <c>maxVisits</c> and
    /// <c>onExceeded</c> are both present or both absent — a cap with nowhere to route to, or a route with no
    /// cap behind it, would only be discovered after a run had already paid for the agent turns that hit it;
    /// a declared <c>terminal</c> is <c>succeeded</c> or <c>failed</c>, compared case-insensitively to match
    /// <see cref="WorkflowInterpreter"/>'s own parsing — <c>cancelled</c> is an operator action through
    /// <c>CancelAsync</c>, not a destination a process graph gets to declare, and any other value would
    /// otherwise validate cleanly and only fail once a run reached it; and a node is exactly one of task, gate
    /// or terminal.
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

            if (node.MaxVisits is not null && node.OnExceeded is null)
            {
                errors.Add($"node '{name}' declares 'maxVisits' but no 'onExceeded' target");
            }

            if (node.OnExceeded is not null && node.MaxVisits is null)
            {
                errors.Add($"node '{name}' declares 'onExceeded' but no 'maxVisits' cap");
            }

            if (node.Terminal is not null &&
                !node.Terminal.Equals("succeeded", StringComparison.OrdinalIgnoreCase) &&
                !node.Terminal.Equals("failed", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"node '{name}' has an unrecognized terminal status '{node.Terminal}' (must be 'succeeded' or 'failed'; 'cancelled' is an operator action, not a declared destination)");
            }

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
            if (node.Agent is not null && !await resolver.AgentExistsAsync(node.Agent, ct).ConfigureAwait(false))
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
