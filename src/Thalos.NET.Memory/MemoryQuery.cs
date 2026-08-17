namespace Thalos.Memory;

/// <summary>Filter + paging for <see cref="IMemoryStore.ListAsync"/>/<see cref="IMemoryStore.StreamAsync"/>. Null filters mean "no filter".</summary>
public sealed record MemoryQuery
{
    /// <summary>Upper bound of <see cref="PageSize"/>.</summary>
    public const int MaxPageSize = 100;

    /// <summary>Owners to include. Null/empty = all owners (store level only; <c>IMemoryService.ListAsync</c> requires at least one).</summary>
    public IReadOnlyList<string>? OwnerIds { get; init; }

    /// <summary>Only records pinned to this agent. Null = no agent filter (owner-wide and pinned alike).</summary>
    public AgentId? AgentId { get; init; }

    /// <summary>Only records of one of these kinds. Null/empty = all kinds.</summary>
    public IReadOnlyList<MemoryKind>? Kinds { get; init; }

    /// <summary>Every listed tag must be present on the record. Query tags are normalised like stored tags (trimmed, lower-cased; see <see cref="MemoryRules.NormalizeTags"/>) and matched ordinally.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Include archived (soft-deleted) records; default false.</summary>
    public bool IncludeArchived { get; init; }

    /// <summary>Filter on <see cref="MemoryRecord.IndexPending"/>; null = both.</summary>
    public bool? IndexPending { get; init; }

    /// <summary>1-based page.</summary>
    public int Page { get; init; } = 1;

    /// <summary>1..<see cref="MaxPageSize"/>; stores clamp.</summary>
    public int PageSize { get; init; } = 50;

    /// <summary>The filter semantics every store must implement (paging excluded).</summary>
    public bool Matches(MemoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (OwnerIds is { Count: > 0 } && !OwnerIds.Contains(record.OwnerId, StringComparer.Ordinal))
        {
            return false;
        }

        if (AgentId is { } agent && record.AgentId != agent)
        {
            return false;
        }

        if (Kinds is { Count: > 0 } && !Kinds.Contains(record.Kind))
        {
            return false;
        }

        if (Tags is { Count: > 0 })
        {
            foreach (var tag in Tags)
            {
                var normalized = MemoryRules.NormalizeTag(tag);
                if (string.IsNullOrEmpty(normalized) || !record.Tags.Contains(normalized, StringComparer.Ordinal))
                {
                    return false;
                }
            }
        }

        if (!IncludeArchived && record.IsArchived)
        {
            return false;
        }

        return IndexPending is not { } pending || record.IndexPending == pending;
    }
}

/// <summary>One page of records (newest <c>UpdatedAt</c> first) with the total match count.</summary>
public sealed record MemoryPage(IReadOnlyList<MemoryRecord> Items, int Page, int PageSize, int TotalCount);

/// <summary>Partial update; null members are left unchanged. Setting Text/Tags/Importance/IsArchived bumps <c>UpdatedAt</c>; <see cref="IndexPending"/> alone does not.</summary>
public sealed record MemoryUpdate
{
    /// <summary>New text (validated like <see cref="MemoryRecord.Text"/>).</summary>
    public string? Text { get; init; }

    /// <summary>New tags (normalised by the store; an empty list clears them).</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>New importance in 0..1.</summary>
    public double? Importance { get; init; }

    /// <summary>Archive (true) or restore (false).</summary>
    public bool? IsArchived { get; init; }

    /// <summary>Set/clear the index-pending flag (bookkeeping; does not bump <c>UpdatedAt</c>).</summary>
    public bool? IndexPending { get; init; }

    /// <summary>True when the update changes user-visible content (and must bump <c>UpdatedAt</c>).</summary>
    public bool TouchesContent => Text is not null || Tags is not null || Importance is not null || IsArchived is not null;
}
