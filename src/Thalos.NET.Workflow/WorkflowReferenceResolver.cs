using Thalos.Skills;

namespace Thalos.Workflow;

/// <summary>
/// The default <see cref="IWorkflowReferenceResolver"/>: agent and skill existence resolved over Thalos's own
/// <see cref="IAgentCatalog"/> and <see cref="ISkillStore"/>. Both are Thalos contracts, so this needs no host —
/// unlike <see cref="IProcessDefinitionSource"/>, where the definitions themselves come from a host-supplied,
/// git-backed implementation.
/// </summary>
/// <remarks>
/// Agent names are matched case-insensitively against <see cref="AgentDefinition.Name"/> — the same comparison
/// Daedalus's <c>AgentNameValidator</c> uses to check a configured agent name against
/// <see cref="IAgentCatalog.Agents"/>. A stricter comparison here would mean a process file that validates
/// cleanly at load time under that looser match could still fail every node once a run went live, at the cost of
/// the agent turns already spent reaching it.
/// </remarks>
public sealed class WorkflowReferenceResolver(IAgentCatalog agentCatalog, ISkillStore skillStore) : IWorkflowReferenceResolver
{
    private readonly IAgentCatalog _agentCatalog = agentCatalog ?? throw new ArgumentNullException(nameof(agentCatalog));
    private readonly ISkillStore _skillStore = skillStore ?? throw new ArgumentNullException(nameof(skillStore));

    /// <inheritdoc/>
    public ValueTask<AgentId?> ResolveAgentIdAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        foreach (var agent in _agentCatalog.Agents)
        {
            if (string.Equals(agent.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return ValueTask.FromResult<AgentId?>(agent.Id);
            }
        }

        return ValueTask.FromResult<AgentId?>(null);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A skill whose file has disappeared (<see cref="SkillDocument.IsActive"/> false) does not count as
    /// existing here: <see cref="ISkillStore.GetAsync"/> still returns an inactive document (its own contract
    /// says callers decide), but a process referencing a skill that is no longer in the repository should fail
    /// validation the same as one that was never there.
    /// </remarks>
    public async ValueTask<bool> SkillExistsAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!SkillName.TryParse(name, out var skillName))
        {
            return false;
        }

        var result = await _skillStore.GetAsync(skillName, ct).ConfigureAwait(false);
        return result.IsSuccess && result.Value.IsActive;
    }
}
