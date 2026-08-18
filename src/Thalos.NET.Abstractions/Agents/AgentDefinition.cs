using ZeroAlloc.Validation;

namespace Thalos;

/// <summary>Declarative description of an agent. Bind from configuration or build in code.</summary>
[Validate]
public sealed record AgentDefinition
{
    /// <summary>Stable identity of the agent; sessions and the agent-factory cache key on it.</summary>
    public required AgentId Id { get; init; }

    /// <summary>Display name (1–64 chars) shown in logs, telemetry and to the model as the agent's name.</summary>
    [NotEmpty] [MaxLength(64)]
    public required string Name { get; init; }

    /// <summary>Optional human-readable description; forwarded to the Agent Framework agent (not part of the prompt).</summary>
    public string Description { get; init; } = "";

    /// <summary>System instructions sent on every model call.</summary>
    [NotEmpty]
    public required string Instructions { get; init; }

    /// <summary>Provider model id. Null → provider default.</summary>
    public string? Model { get; init; }

    /// <summary>Per-call output token cap. Null → provider default.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>Glob allow-list over qualified tool names ("source__tool"). Default: everything.</summary>
    public IReadOnlyList<string> Tools { get; init; } = ["*"];

    /// <summary>
    /// Glob allow-list over skill names (Thalos.NET.Skills). Unlike <see cref="Tools"/> the default is <em>empty</em>: a skill
    /// catalogue is injected into every turn, so an agent opts in explicitly (<c>["*"]</c> for all). Added in 0.3.0; definitions
    /// serialised before that simply carry an empty list. Compared by value by the agent factory.
    /// </summary>
    public IReadOnlyList<string> Skills { get; init; } = [];

    /// <summary>
    /// Per-agent memory settings (Thalos.NET.Memory); null = host defaults. Compared by value by the agent factory. Added in 0.2.0:
    /// definitions serialised before that (or by hosts without memory) simply carry <see langword="null"/> here.
    /// </summary>
    public AgentMemorySettings? Memory { get; init; }
}
