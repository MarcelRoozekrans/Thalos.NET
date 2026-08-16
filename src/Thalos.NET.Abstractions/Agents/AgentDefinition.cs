using ZeroAlloc.Validation;

namespace Thalos;

/// <summary>Declarative description of an agent. Bind from configuration or build in code.</summary>
[Validate]
public sealed record AgentDefinition
{
    public required AgentId Id { get; init; }

    [NotEmpty] [MaxLength(64)]
    public required string Name { get; init; }

    public string Description { get; init; } = "";

    [NotEmpty]
    public required string Instructions { get; init; }

    /// <summary>Provider model id. Null → provider default.</summary>
    public string? Model { get; init; }

    public int? MaxOutputTokens { get; init; }

    /// <summary>Glob allow-list over qualified tool names ("source__tool"). Default: everything.</summary>
    public IReadOnlyList<string> Tools { get; init; } = ["*"];
}
