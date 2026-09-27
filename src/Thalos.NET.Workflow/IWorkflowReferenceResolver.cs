namespace Thalos.Workflow;

/// <summary>
/// Resolves whether a skill or host action name referenced by a <see cref="ProcessDefinition"/> is actually
/// registered with the host running the workflow engine, and resolves an agent name to the <see cref="AgentId"/>
/// it identifies.
/// <see cref="ProcessValidator.ValidateAsync"/> accepts <see langword="null"/> for this interface to run
/// shape-only validation — the graph-structure rules that hold regardless of which host is running the process —
/// which keeps the validator unit-testable without spinning up a host.
/// </summary>
public interface IWorkflowReferenceResolver
{
    /// <summary>Whether a skill named <paramref name="name"/> is registered with the host.</summary>
    ValueTask<bool> SkillExistsAsync(string name, CancellationToken ct);

    /// <summary>
    /// Resolves the agent named <paramref name="name"/> to its <see cref="AgentId"/>, or
    /// <see langword="null"/> if none is registered under that name. <c>ProcessNode.Agent</c> is a
    /// human-authored name in a git-reviewed process file, not the <see cref="AgentId"/> ULID itself — a real
    /// host implements this over its own <c>IAgentCatalog</c> so both <see cref="ProcessValidator"/> (existence,
    /// at load time) and <c>WorkflowNodeDispatcher</c> (the id it actually needs, at dispatch time) go through
    /// the same lookup. There is deliberately no separate <c>AgentExistsAsync</c>: two lookups that both have to
    /// answer "does this agent exist" and must always agree will eventually be implemented inconsistently, and
    /// the failure mode is the worst one this phase has — a process validates cleanly at load and then fails per
    /// node, after a run is already live and paying for turns. One method makes that inconsistency
    /// unrepresentable instead of merely documented.
    /// </summary>
    ValueTask<AgentId?> ResolveAgentIdAsync(string name, CancellationToken ct);

    /// <summary>
    /// Whether an <see cref="IWorkflowHostAction"/> named <paramref name="name"/> is registered with the host,
    /// compared ordinally as <see cref="IWorkflowHostAction.Name"/> documents. <see cref="ProcessValidator"/> asks
    /// this for every action node's <see cref="ProcessNode.Action"/>, so a process naming an action the host never
    /// registered is rejected at load time instead of failing its run once it reaches that node. Answer from the
    /// same registrations <c>WorkflowNodeDispatcher</c> is given, or the two can disagree: a process that validates
    /// here would still fail at dispatch with "not registered".
    /// </summary>
    /// <remarks>
    /// Deliberately abstract, with no default body. A default answering <see langword="false"/> would fail closed,
    /// but it would also hide that a resolver was never taught about actions; without a default, that is a
    /// compile error in the resolver instead.
    /// </remarks>
    ValueTask<bool> HostActionExistsAsync(string name, CancellationToken ct);
}
