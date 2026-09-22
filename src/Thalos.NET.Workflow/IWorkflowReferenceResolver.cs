namespace Thalos.Workflow;

/// <summary>
/// Resolves whether an agent or skill name referenced by a <see cref="ProcessDefinition"/> is actually registered
/// with the host running the workflow engine. <see cref="ProcessValidator.ValidateAsync"/> accepts
/// <see langword="null"/> for this interface to run shape-only validation — the graph-structure rules that hold
/// regardless of which host is running the process — which keeps the validator unit-testable without spinning up
/// a host.
/// </summary>
public interface IWorkflowReferenceResolver
{
    /// <summary>Whether an agent named <paramref name="name"/> is registered with the host.</summary>
    ValueTask<bool> AgentExistsAsync(string name, CancellationToken ct);

    /// <summary>Whether a skill named <paramref name="name"/> is registered with the host.</summary>
    ValueTask<bool> SkillExistsAsync(string name, CancellationToken ct);

    /// <summary>
    /// Resolves the agent named <paramref name="name"/> to its <see cref="AgentId"/>, or
    /// <see langword="null"/> if none is registered under that name. <c>ProcessNode.Agent</c> is a
    /// human-authored name in a git-reviewed process file, not the <see cref="AgentId"/> ULID
    /// itself — a real host implements this over its own <c>IAgentCatalog</c> so
    /// <c>WorkflowNodeDispatcher</c> can turn that name into the id <c>SubagentRunRequest.AgentId</c>
    /// actually needs, at dispatch time rather than expecting the file to carry a machine identifier.
    /// </summary>
    ValueTask<AgentId?> ResolveAgentIdAsync(string name, CancellationToken ct);
}
