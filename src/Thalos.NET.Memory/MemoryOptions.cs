namespace Thalos.Memory;

/// <summary>Host-wide memory configuration (section <c>Thalos:Memory</c>).</summary>
public sealed class MemoryOptions
{
    public const string SectionName = "Thalos:Memory";

    /// <summary>Master switch for auto-recall and tools (per-agent <c>AgentMemorySettings.Enabled</c> overrides auto-recall).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Owner id under which host code writes project-wide knowledge (e.g. <c>"daedalus"</c>); read by every caller, written only by host code.</summary>
    public string? SharedOwnerId { get; set; }

    public RecallOptions Recall { get; set; } = new();

    public DedupeOptions Dedupe { get; set; } = new();

    /// <summary>Register the <c>memory</c> tool source (<c>memory__remember/recall/forget/list</c>).</summary>
    public bool ExposeTools { get; set; } = true;
}

public sealed class DedupeOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Similarity at or above which a new memory refreshes an existing one instead of inserting.</summary>
    public double Threshold { get; set; } = 0.95;
}
