using ZeroAlloc.Authorization;

namespace Thalos;

/// <summary>One detached run: an agent, a task, and the identity it runs as. See <c>ISubagentRunner</c>.</summary>
public sealed record SubagentRunRequest
{
    /// <summary>The agent to run. Resolved through <c>IAgentCatalog</c>; a subagent is an ordinary agent definition.</summary>
    public required AgentId AgentId { get; init; }

    /// <summary>The instruction for this run, sent as the single user message of a single turn.</summary>
    public required string Task { get; init; }

    /// <summary>
    /// The identity the run executes as. Never inferred: a detached run has no inbound request to derive one from,
    /// so the caller supplies it — normally a narrow, configured principal rather than a human's own context.
    /// </summary>
    public required ISecurityContext Caller { get; init; }

    /// <summary>Token and wall-clock ceilings. Defaults to <see cref="SubagentBudget.Default"/>.</summary>
    public SubagentBudget Budget { get; init; } = SubagentBudget.Default;

    /// <summary>
    /// Nesting depth; 0 for a run started by a host. Refused above <c>SubagentOptions.MaxDepth</c>. Nothing in
    /// Thalos increments this today — sagas are compile-time, so there is no recursion to prevent yet. It exists so
    /// that a future in-turn delegation tool cannot recurse without bound.
    /// </summary>
    public int Depth { get; init; }

    /// <summary>The session that caused this run, when there is one. Telemetry lineage only; never authorization.</summary>
    public SessionId? ParentSessionId { get; init; }
}
