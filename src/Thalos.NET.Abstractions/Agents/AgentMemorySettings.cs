namespace Thalos;

/// <summary>Per-agent memory overrides (Thalos.NET.Memory). Null members fall back to the host-wide <c>MemoryOptions</c>.</summary>
public sealed record AgentMemorySettings
{
    /// <summary>Whether auto-recall runs for this agent. Null → <c>MemoryOptions.Enabled</c>.</summary>
    public bool? Enabled { get; init; }

    /// <summary>Max memories injected per turn. Null → <c>MemoryOptions.Recall.TopK</c>.</summary>
    public int? TopK { get; init; }
}
