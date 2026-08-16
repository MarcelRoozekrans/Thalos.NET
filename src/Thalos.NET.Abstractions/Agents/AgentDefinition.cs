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
}
