using ZeroAlloc.Results;

namespace Thalos.Memory;

/// <summary>Facade over <see cref="IMemoryStore"/> + <see cref="IMemoryIndex"/>: the only entry point tools, providers and host code use.</summary>
public interface IMemoryService
{
    /// <summary>
    /// Validate (owner must be a non-blank, non-anonymous id) → dedupe → create → index.
    /// Dedupe (same owner, no shared owner, similarity ≥ <see cref="DedupeOptions.Threshold"/>) refreshes the existing record instead of
    /// inserting: only <c>Importance</c> (max of both) and <c>UpdatedAt</c> change — the existing text, tags and source are kept; it is
    /// best-effort under concurrency (two simultaneous near-duplicates may both insert). An index failure leaves the new record with
    /// <c>IndexPending</c>, raises <see cref="MemoryIndexPendingEvent"/> instead of <see cref="MemoryStoredEvent"/>, and still returns success.
    /// </summary>
    ValueTask<Result<MemoryRecord, AgentError>> RememberAsync(RememberRequest request, CancellationToken ct);

    /// <summary>
    /// Search within <paramref name="scope"/> (over-fetching 2 × TopK), hydrate, drop archived/missing/out-of-scope, order by score ↓
    /// importance ↓ UpdatedAt ↓ id, apply TopK/MaxChars, mark recalled. Fewer than TopK may come back when many hits were archived or
    /// do not fit the budget. Index failures are returned (callers decide); blank query → empty.
    /// </summary>
    ValueTask<Result<IReadOnlyList<RecalledMemory>, AgentError>> RecallAsync(string query, MemoryScope scope, RecallOptions options, CancellationToken ct);

    /// <summary>Archive (<paramref name="hard"/> = false) or delete a memory owned by <c>scope.OwnerId</c>; other owners → <see cref="AgentErrorCode.MemoryForbidden"/>.</summary>
    ValueTask<UnitResult<AgentError>> ForgetAsync(MemoryId id, MemoryScope scope, bool hard, CancellationToken ct);

    /// <summary>Paged listing; <see cref="MemoryQuery.OwnerIds"/> must contain at least one owner.</summary>
    ValueTask<Result<MemoryPage, AgentError>> ListAsync(MemoryQuery query, CancellationToken ct);

    /// <summary>
    /// Re-embeds pending (or, with <c>PendingOnly = false</c>, all non-archived) records in batches and clears <c>IndexPending</c>; fails
    /// fast when the index probe says unavailable. Full mode re-embeds but does not purge stale vectors (those are dropped at recall).
    /// See <see cref="ReindexReport"/> for how failures are counted. A store that throws while streaming records aborts the run with
    /// <see cref="AgentErrorCode.MemoryStoreFailed"/> (batches flushed before that keep their cleared flags; the rest stays pending).
    /// </summary>
    ValueTask<Result<ReindexReport, AgentError>> ReindexAsync(ReindexOptions options, CancellationToken ct);
}
