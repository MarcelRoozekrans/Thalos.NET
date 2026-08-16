using System.Diagnostics.CodeAnalysis;

namespace Thalos;

/// <summary>Registered agent definitions.</summary>
public interface IAgentCatalog
{
    /// <summary>All registered definitions, in registration order.</summary>
    IReadOnlyList<AgentDefinition> Agents { get; }

    /// <summary>Looks up a definition by id; <see langword="false"/> (and <see langword="null"/>) when not registered.</summary>
    bool TryGet(AgentId id, [MaybeNullWhen(false)] out AgentDefinition definition);
}
