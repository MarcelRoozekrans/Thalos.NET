using System.Diagnostics.CodeAnalysis;
using Thalos;

namespace Thalos.Tests.Workflow;

/// <summary>
/// An <see cref="IAgentCatalog"/> over a fixed list the test supplies, with no revision-pinning support — the
/// same shape <see cref="WorkflowReferenceResolverTests"/> already relied on as a private nested type before
/// <see cref="CatalogRunManifestResolverTests"/> needed the identical fake, at which point it moved here so both
/// share one implementation instead of two copies drifting apart.
/// </summary>
internal sealed class FakeAgentCatalog(IReadOnlyList<AgentDefinition> agents) : IAgentCatalog
{
    public IReadOnlyList<AgentDefinition> Agents { get; } = agents;

    public bool TryGet(AgentId id, [MaybeNullWhen(false)] out AgentDefinition definition)
    {
        definition = Agents.FirstOrDefault(a => a.Id == id);
        return definition is not null;
    }
}
