using System.Runtime.InteropServices;

namespace Thalos.Memory;

/// <summary>Input of <c>IMemoryService.RememberAsync</c>. Owner comes from the caller's security context or the host's shared owner — never from a model.</summary>
public sealed record RememberRequest
{
    public required string OwnerId { get; init; }
    public AgentId? AgentId { get; init; }
    public required string Text { get; init; }
    public MemoryKind Kind { get; init; } = MemoryKind.Note;
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string Source { get; init; } = "";
    public double Importance { get; init; } = 0.5;
}

/// <summary>
/// Recall budget. Bindable (class with setters) because it is part of <see cref="MemoryOptions"/>. It is a bound singleton:
/// never mutate it per call — copy it (Task 13 builds a fresh instance when applying <c>AgentMemorySettings.TopK</c>).
/// </summary>
public sealed class RecallOptions
{
    /// <summary>Max memories per recall; values below 1 are treated as 1.</summary>
    public int TopK { get; set; } = 5;

    public double MinScore { get; set; } = 0.6;

    /// <summary>Total character budget for the recalled texts; 0 or negative = no budget (TopK alone caps the result).</summary>
    public int MaxChars { get; set; } = 2000;
}

/// <summary>What an index applies before returning hits.</summary>
[StructLayout(LayoutKind.Auto)] // MA0008: all-blittable
public readonly record struct MemorySearchOptions(int TopK, double MinScore);

/// <summary>An index hit; <paramref name="Score"/> is a similarity in [0, 1] (cosine).</summary>
[StructLayout(LayoutKind.Auto)] // MA0008: all-blittable
public readonly record struct MemoryHit(MemoryId Id, double Score);

/// <summary>A hydrated recall result.</summary>
public sealed record RecalledMemory(MemoryRecord Record, double Score);

/// <summary>Index health from <c>IMemoryIndex.ProbeAsync</c>.</summary>
public sealed record MemoryIndexHealth(bool Available, int? Dimensions, string? Detail = null);

public sealed record ReindexOptions
{
    /// <summary>True = only records with <c>IndexPending</c>; false = every non-archived record.</summary>
    public bool PendingOnly { get; init; } = true;

    public int BatchSize { get; init; } = 32;
}

/// <summary>
/// Outcome of <c>IMemoryService.ReindexAsync</c>. <paramref name="Failed"/> counts records of a batch whose upsert failed (none of them
/// were written) plus records whose vector was written but whose <c>IndexPending</c> flag could not be cleared — both stay pending and
/// are re-embedded by the next run. <c>Scanned == Indexed + Failed</c>.
/// </summary>
public sealed record ReindexReport(int Scanned, int Indexed, int Failed);
