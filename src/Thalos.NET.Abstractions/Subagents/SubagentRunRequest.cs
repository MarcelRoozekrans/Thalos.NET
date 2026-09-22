using ZeroAlloc.Authorization;

namespace Thalos;

/// <summary>One detached run: an agent, a task, and the identity it runs as. See <see cref="ISubagentRunner"/>.</summary>
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

    /// <summary>
    /// Token and wall-clock ceilings for this run. <see langword="null"/> means "use the host's configured default":
    /// the runner resolves an omitted budget to <c>SubagentOptions.DefaultBudget</c>, so a value set here always wins
    /// over the host default, never the other way around. Left <see langword="null"/> rather than defaulted to
    /// <see cref="SubagentBudget.Default"/> here so a host-configured <c>DefaultBudget</c> is not silently shadowed by
    /// this record's own default the moment a caller builds a request without naming a budget.
    /// </summary>
    public SubagentBudget? Budget { get; init; }

    /// <summary>
    /// Nesting depth; 0 for a run started by a host. Refused above <c>SubagentOptions.MaxDepth</c>. Nothing in
    /// Thalos increments this today — sagas are compile-time, so there is no recursion to prevent yet. It exists so
    /// that a future in-turn delegation tool cannot recurse without bound.
    /// </summary>
    public int Depth { get; init; }

    /// <summary>The session that caused this run, when there is one. Telemetry lineage only; never authorization.</summary>
    public SessionId? ParentSessionId { get; init; }

    /// <summary>
    /// When set, this run must report its result through the tool-call schema described here rather than through
    /// free text — see <see cref="OutcomeToolSchema"/> for why that distinction is load-bearing. <see langword="null"/>
    /// for a run with no closed-set outcome to report (a plain sequence step with nothing to branch on).
    /// </summary>
    public OutcomeToolSchema? RequiredOutcome { get; init; }
}
