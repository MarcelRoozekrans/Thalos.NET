namespace Thalos.Memory;

/// <summary>Host-wide memory configuration (section <c>Thalos:Memory</c>).</summary>
public sealed class MemoryOptions
{
    /// <summary>Configuration section bound by <c>UseMemory(IConfiguration)</c>.</summary>
    public const string SectionName = "Thalos:Memory";

    /// <summary>Master switch for auto-recall and tools (per-agent <c>AgentMemorySettings.Enabled</c> overrides auto-recall).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Owner id under which host code writes project-wide knowledge (e.g. <c>"daedalus"</c>); read by every caller, written only by host code.</summary>
    public string? SharedOwnerId { get; set; }

    /// <summary>Default recall budget (auto-recall and <c>memory__recall</c>); per-agent <c>AgentMemorySettings.TopK</c> overrides <see cref="RecallOptions.TopK"/>.</summary>
    public RecallOptions Recall { get; set; } = new();

    /// <summary>Near-duplicate handling on remember.</summary>
    public DedupeOptions Dedupe { get; set; } = new();

    /// <summary>Register the <c>memory</c> tool source (<c>memory__remember/recall/forget/list</c>).</summary>
    public bool ExposeTools { get; set; } = true;
}

/// <summary>Near-duplicate handling on remember: a sufficiently similar existing memory of the same owner/agent scope is refreshed instead of inserting.</summary>
public sealed class DedupeOptions
{
    /// <summary>Whether dedupe runs at all (it needs an available index).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Similarity in (0, 1] at or above which a new memory refreshes an existing one instead of inserting. A value outside (0, 1] (or NaN) disables dedupe (warned once).</summary>
    public double Threshold { get; set; } = 0.95;
}
