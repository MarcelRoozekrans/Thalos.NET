namespace Thalos.Skills.Charters;

/// <summary>
/// The config-owned half of a chartered agent: identity and security envelope. Prose, model and skills come from the
/// role's active <see cref="RoleCharter"/> instead — see <see cref="CharteredAgentCatalog.Set"/> for how the two are
/// composed into one <see cref="AgentDefinition"/>.
/// </summary>
public sealed record AgentEnvelope
{
    /// <summary>Stable identity of the composed agent; sessions and the agent factory cache key on it.</summary>
    public required AgentId Id { get; init; }

    /// <summary>
    /// Must equal the charter's <see cref="RoleCharter.Role"/> — this is how an envelope and a charter are matched up.
    /// A name that collides with a <c>ThalosOptions.Agents</c> entry fails options validation at start.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>Glob allow-list over qualified tool names. Always wins over anything a charter could ask for: a charter never names tools.</summary>
    public required IReadOnlyList<string> Tools { get; init; }

    /// <summary>Per-call output token cap. Null → provider default.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>Per-agent memory settings (Thalos.NET.Memory); null = host defaults.</summary>
    public AgentMemorySettings? Memory { get; init; }
}
