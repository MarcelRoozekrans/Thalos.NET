using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;

namespace Thalos.Agents;

/// <summary><see cref="IAgentCatalog"/> over <see cref="ThalosOptions.Agents"/> (snapshot taken at construction).</summary>
public sealed class OptionsAgentCatalog : IAgentCatalog
{
    private readonly Dictionary<AgentId, AgentDefinition> _byId;

    /// <summary>Creates a catalog from <see cref="ThalosOptions.Agents"/>.</summary>
    public OptionsAgentCatalog(IOptions<ThalosOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Agents = options.Value.Agents.ToList();
        _byId = Agents.ToDictionary(a => a.Id);
    }

    /// <inheritdoc />
    public IReadOnlyList<AgentDefinition> Agents { get; }

    /// <inheritdoc />
    public bool TryGet(AgentId id, [MaybeNullWhen(false)] out AgentDefinition definition) => _byId.TryGetValue(id, out definition);
}
