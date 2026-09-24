using System.Diagnostics.CodeAnalysis;

namespace Thalos;

/// <summary>Registered agent definitions.</summary>
public interface IAgentCatalog
{
    /// <summary>All registered definitions, in registration order.</summary>
    IReadOnlyList<AgentDefinition> Agents { get; }

    /// <summary>Looks up a definition by id; <see langword="false"/> (and <see langword="null"/>) when not registered.</summary>
    bool TryGet(AgentId id, [MaybeNullWhen(false)] out AgentDefinition definition);

    /// <summary>
    /// Looks up a definition by id, pinned to <paramref name="revision"/>. <paramref name="revision"/> <see langword="null"/>
    /// delegates to <see cref="TryGet(AgentId, out AgentDefinition)"/> — today's behavior, unchanged. A non-null revision this
    /// default implementation cannot resolve is refused (<see langword="false"/>, <paramref name="definition"/>
    /// <see langword="null"/>): a catalog that knows no revisions never serves the current definition in place of a pinned one.
    /// Override this to serve pinned revisions (see <c>CharteredAgentCatalog</c>).
    /// </summary>
    bool TryGet(AgentId id, string? revision, [MaybeNullWhen(false)] out AgentDefinition definition)
    {
        if (revision is null)
        {
            return TryGet(id, out definition);
        }

        definition = null;
        return false;
    }
}
