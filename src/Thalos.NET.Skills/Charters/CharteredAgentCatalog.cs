using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;

namespace Thalos.Skills.Charters;

/// <summary>
/// <see cref="IAgentCatalog"/> that serves config agents unchanged and composes one <see cref="AgentDefinition"/> per
/// <see cref="AgentEnvelope"/> from its role's active <see cref="RoleCharter"/>.
/// </summary>
/// <remarks>
/// <para>
/// Composition rule, enforced only by <see cref="Set"/>: <c>Tools</c>, <c>MaxOutputTokens</c> and <c>Memory</c> always
/// come from the envelope; <c>Description</c>, <c>Instructions</c>, <c>Model</c>, <c>Skills</c> and <c>Revision</c>
/// (the charter's content hash) always come from the charter. An envelope whose role has no active charter composes
/// into no agent at all — it is simply absent from <see cref="Agents"/> and <see cref="TryGet(AgentId, out AgentDefinition)"/>,
/// never served with empty instructions. <see cref="CharterSyncService"/> is what turns a missing charter into a
/// startup failure.
/// </para>
/// <para>
/// The catalog holds two generations of state: the config agents and envelope roster, fixed at construction like
/// <see cref="Agents.OptionsAgentCatalog"/>, and the charter-derived snapshot that <see cref="Set"/> republishes on
/// every sync. A reader never observes a torn update — <see cref="Set"/> builds the whole next snapshot off to the
/// side (two dictionaries) and swaps a single <see langword="volatile"/> field.
/// </para>
/// </remarks>
public sealed class CharteredAgentCatalog : IAgentCatalog
{
    private readonly List<AgentDefinition> _configAgents;
    private readonly Dictionary<AgentId, AgentDefinition> _configById;
    private readonly List<AgentEnvelope> _envelopes;
    private readonly Dictionary<string, AgentEnvelope> _envelopesByRole;
    private volatile Snapshot _snapshot;

    /// <summary>
    /// Creates the catalog from <see cref="ThalosOptions.Agents"/> and <see cref="CharterOptions.Envelopes"/>. No
    /// charter is composed yet — <see cref="Agents"/> and <see cref="TryGet(AgentId, out AgentDefinition)"/> serve
    /// only the config agents until <see cref="Set"/> is called (normally by <see cref="CharterSyncService"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">Two config agents share the same <see cref="AgentDefinition.Id"/>.</exception>
    public CharteredAgentCatalog(IOptions<ThalosOptions> thalos, IOptions<CharterOptions> charters)
    {
        ArgumentNullException.ThrowIfNull(thalos);
        ArgumentNullException.ThrowIfNull(charters);

        var configAgents = thalos.Value.Agents.ToList();
        var configById = new Dictionary<AgentId, AgentDefinition>();
        foreach (var definition in configAgents)
        {
            if (!configById.TryAdd(definition.Id, definition))
            {
                throw new InvalidOperationException(
                    $"Duplicate agent id '{definition.Id}' ('{configById[definition.Id].Name}' and '{definition.Name}'). Agent ids must be unique.");
            }
        }

        var envelopes = charters.Value.Envelopes.ToList();
        var envelopesByRole = new Dictionary<string, AgentEnvelope>(StringComparer.Ordinal);
        foreach (var envelope in envelopes)
        {
            envelopesByRole[envelope.Name] = envelope;
        }

        _configAgents = configAgents;
        _configById = configById;
        _envelopes = envelopes;
        _envelopesByRole = envelopesByRole;
        _snapshot = new Snapshot(configAgents, configById, new Dictionary<(AgentId, string), AgentDefinition>());
    }

    /// <inheritdoc />
    public IReadOnlyList<AgentDefinition> Agents => _snapshot.Agents;

    /// <inheritdoc />
    public bool TryGet(AgentId id, [MaybeNullWhen(false)] out AgentDefinition definition) => _snapshot.Current.TryGetValue(id, out definition);

    /// <inheritdoc />
    /// <remarks><paramref name="revision"/> is a charter <see cref="RoleCharter.ContentHash"/>; an unknown pin fails and never falls back to the current definition.</remarks>
    public bool TryGet(AgentId id, string? revision, [MaybeNullWhen(false)] out AgentDefinition definition)
    {
        if (revision is null)
        {
            return TryGet(id, out definition);
        }

        return _snapshot.ByRevision.TryGetValue((id, revision), out definition);
    }

    /// <summary>
    /// Republishes the charter-composed half of the catalog from every role version passed in — normally
    /// <see cref="CharterSyncService"/> handing over the store's whole set, active and inactive, on every sync. Passing
    /// every version (not just the active ones) is what lets a pinned revision stay resolvable by
    /// <see cref="TryGet(AgentId, string?, out AgentDefinition)"/> after its role's charter moves on. A version whose
    /// <see cref="RoleCharter.Role"/> matches no <see cref="AgentEnvelope.Name"/> claims no tools and composes into no
    /// agent at all.
    /// </summary>
    /// <param name="versions">Every charter version known to the store (active and inactive).</param>
    public void Set(IReadOnlyList<RoleCharter> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);

        var byRevision = new Dictionary<(AgentId Id, string Revision), AgentDefinition>();
        var envelopeCurrent = new Dictionary<AgentId, AgentDefinition>();

        for (var i = 0; i < versions.Count; i++)
        {
            var charter = versions[i];
            if (!_envelopesByRole.TryGetValue(charter.Role, out var envelope))
            {
                continue;
            }

            var definition = Compose(envelope, charter);
            byRevision[(envelope.Id, charter.ContentHash)] = definition;
            if (charter.IsActive)
            {
                envelopeCurrent[envelope.Id] = definition;
            }
        }

        var current = new Dictionary<AgentId, AgentDefinition>(_configById);
        foreach (var (id, definition) in envelopeCurrent)
        {
            current[id] = definition;
        }

        var agents = new List<AgentDefinition>(_configAgents.Count + envelopeCurrent.Count);
        agents.AddRange(_configAgents);
        for (var i = 0; i < _envelopes.Count; i++)
        {
            if (envelopeCurrent.TryGetValue(_envelopes[i].Id, out var definition))
            {
                agents.Add(definition);
            }
        }

        // Built off to the side; readers of the old snapshot see either every field of the old state or every field
        // of the new one, never a mix — a single reference assignment to a volatile field is always atomic in .NET.
        _snapshot = new Snapshot(agents, current, byRevision);
    }

    /// <summary>Tools, output cap and memory always come from <paramref name="envelope"/>; prose, model, skills and the revision pin always come from <paramref name="charter"/>.</summary>
    private static AgentDefinition Compose(AgentEnvelope envelope, RoleCharter charter) => new()
    {
        Id = envelope.Id,
        Name = envelope.Name,
        Description = charter.Description,
        Instructions = charter.Instructions,
        Model = charter.Model,
        Skills = charter.Skills,
        Tools = envelope.Tools,
        MaxOutputTokens = envelope.MaxOutputTokens,
        Memory = envelope.Memory,
        Revision = charter.ContentHash,
    };

    private sealed record Snapshot(
        IReadOnlyList<AgentDefinition> Agents,
        IReadOnlyDictionary<AgentId, AgentDefinition> Current,
        IReadOnlyDictionary<(AgentId Id, string Revision), AgentDefinition> ByRevision);
}
