using Thalos.Skills;
using ZeroAlloc.Results;

namespace Thalos.Workflow;

/// <summary>
/// The default <see cref="IRunManifestResolver"/>: resolves every task node's agent name through the host's own
/// <see cref="IWorkflowReferenceResolver"/> — never by matching <see cref="ProcessNode.Agent"/> against
/// <see cref="IAgentCatalog.Agents"/> directly — so a host that maps a process's human-authored names onto its own
/// agents (a squad role onto a concrete agent, for example) gets that mapping pinned into the manifest exactly as
/// it decided it, not as the process file's author spelled it.
/// </summary>
public sealed class CatalogRunManifestResolver(IWorkflowReferenceResolver references, IAgentCatalog agents, ISkillStore skills) : IRunManifestResolver
{
    private readonly IWorkflowReferenceResolver _references = references ?? throw new ArgumentNullException(nameof(references));
    private readonly IAgentCatalog _agents = agents ?? throw new ArgumentNullException(nameof(agents));
    private readonly ISkillStore _skills = skills ?? throw new ArgumentNullException(nameof(skills));

    /// <inheritdoc/>
    public async ValueTask<Result<RunManifest>> ResolveAsync(ProcessDefinition process, IReadOnlyDictionary<string, string>? documents, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(process);

        var nodes = new Dictionary<string, NodePin>(StringComparer.Ordinal);

        foreach (var (name, node) in process.Nodes)
        {
            // Gates (Await set) and terminals (Terminal set) have no agent or skill to pin.
            if (node.Agent is null)
            {
                continue;
            }

            var agentId = await _references.ResolveAgentIdAsync(node.Agent, ct).ConfigureAwait(false);
            if (agentId is null)
            {
                return Result<RunManifest>.Failure($"node '{name}': agent '{node.Agent}' does not resolve.");
            }

            if (!_agents.TryGet(agentId.Value, out var definition))
            {
                return Result<RunManifest>.Failure($"node '{name}': agent '{node.Agent}' resolved to an id that is not registered in the catalog.");
            }

            if (node.Skill is null || !SkillName.TryParse(node.Skill, out var skillName))
            {
                return Result<RunManifest>.Failure($"node '{name}': '{node.Skill}' is not a valid skill name.");
            }

            var skill = await _skills.GetAsync(skillName, ct).ConfigureAwait(false);
            if (skill.IsFailure || !skill.Value.IsActive)
            {
                return Result<RunManifest>.Failure($"node '{name}': skill '{node.Skill}' is not registered or not active.");
            }

            nodes[name] = new NodePin(definition.Name, definition.Id, definition.Revision, skill.Value.Name.ToString(), skill.Value.ContentHash);
        }

        return Result<RunManifest>.Success(documents is null
            ? new RunManifest { Nodes = nodes }
            : new RunManifest { Nodes = nodes, Documents = documents });
    }
}
