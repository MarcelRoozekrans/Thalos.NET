using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;

namespace Thalos.Agents;

/// <summary><see cref="IAgentCatalog"/> over <see cref="ThalosOptions.Agents"/>.</summary>
/// <remarks>
/// The catalog is a startup snapshot: definitions are copied once at construction and later changes to the options are not
/// observed. For reloadable agents register your own <see cref="IAgentCatalog"/> — <c>AgentFactory</c> value-compares
/// definitions, so swapping a definition rebuilds that agent cleanly on its next turn.
/// </remarks>
public sealed class OptionsAgentCatalog : IAgentCatalog
{
    private readonly Dictionary<AgentId, AgentDefinition> _byId = [];

    /// <summary>Creates a catalog from <see cref="ThalosOptions.Agents"/>.</summary>
    /// <exception cref="InvalidOperationException">Two definitions share the same <see cref="AgentDefinition.Id"/>.</exception>
    public OptionsAgentCatalog(IOptions<ThalosOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Agents = options.Value.Agents.ToList();
        foreach (var definition in Agents)
        {
            if (!_byId.TryAdd(definition.Id, definition))
            {
                throw new InvalidOperationException(
                    $"Duplicate agent id '{definition.Id}' ('{_byId[definition.Id].Name}' and '{definition.Name}'). Agent ids must be unique.");
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AgentDefinition> Agents { get; }

    /// <inheritdoc />
    public bool TryGet(AgentId id, [MaybeNullWhen(false)] out AgentDefinition definition) => _byId.TryGetValue(id, out definition);
}
