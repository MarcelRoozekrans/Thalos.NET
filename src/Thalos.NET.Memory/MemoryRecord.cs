using ZeroAlloc.Validation;

namespace Thalos.Memory;

/// <summary>
/// One curated memory. The store is the source of truth; the index holds its vector. Limits: see <see cref="MemoryRules"/>.
/// Record equality is reference-based on <see cref="Tags"/> (a list); compare with <c>BeEquivalentTo</c> / by field in tests.
/// </summary>
[Validate]
public sealed record MemoryRecord
{
    /// <summary>Maximum length of <see cref="Text"/>.</summary>
    public const int MaxTextLength = 4000;

    /// <summary>Maximum number of <see cref="Tags"/>.</summary>
    public const int MaxTags = 10;

    /// <summary>Maximum length of one tag.</summary>
    public const int MaxTagLength = 32;

    /// <summary>Maximum length of <see cref="Source"/>.</summary>
    public const int MaxSourceLength = 256;

    /// <summary>The record id (also the index's document id).</summary>
    public required MemoryId Id { get; init; }

    /// <summary>Security-context id of the owner (or the host's shared owner). Never set from tool parameters.</summary>
    [NotEmpty]
    public required string OwnerId { get; init; }

    /// <summary>Null = visible to every agent of the owner; otherwise pinned to one agent.</summary>
    public AgentId? AgentId { get; init; }

    /// <summary>The category (<see cref="MemoryKind"/>).</summary>
    public required MemoryKind Kind { get; init; }

    /// <summary>The memory itself: one idea, at most <see cref="MaxTextLength"/> chars.</summary>
    [NotEmpty] [MaxLength(MaxTextLength)]
    public required string Text { get; init; }

    /// <summary>At most <see cref="MaxTags"/> tags of at most <see cref="MaxTagLength"/> chars. Stored lower-case (<see cref="MemoryRules.NormalizeTags"/>) and matched ordinally.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Provenance, e.g. <c>tool:memory__remember</c>, <c>ralph:task/&lt;id&gt;</c>, <c>api</c>. At most <see cref="MaxSourceLength"/> chars.</summary>
    public string Source { get; init; } = "";

    /// <summary>0..1; ties in recall are broken by importance, then recency.</summary>
    public double Importance { get; init; } = 0.5;

    /// <summary>When the record was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Bumped when Text/Tags/Importance/IsArchived change (not by index bookkeeping or recall).</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>When the record was last injected into a turn (null = never).</summary>
    public DateTimeOffset? LastRecalledAt { get; init; }

    /// <summary>How many times the record was recalled.</summary>
    public int RecallCount { get; init; }

    /// <summary>Soft-deleted: never recalled, listed only on request.</summary>
    public bool IsArchived { get; init; }

    /// <summary>Stored but not (yet) present in the index; repaired by <c>IMemoryService.ReindexAsync</c>.</summary>
    public bool IndexPending { get; init; }
}
