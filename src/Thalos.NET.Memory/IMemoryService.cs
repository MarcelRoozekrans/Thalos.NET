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
    /// importance ↓ UpdatedAt ↓ id, apply TopK/MaxChars, mark recalled — <see cref="MemoryRecallTier.Semantic"/>. When the index probe
    /// says unavailable or the raw search comes back with zero hits, this falls through to the store instead of failing: the scope's
    /// partitions ordered by <c>UpdatedAt</c> descending, the same TopK/MaxChars budget applied — <see cref="MemoryRecallTier.Recency"/>,
    /// or <see cref="MemoryRecallTier.None"/> when there are no rows in scope at all. Fewer than TopK may come back when many hits were
    /// archived, out of budget, or the store simply has fewer. Blank query or blank owner → <see cref="MemoryRecallTier.None"/>, empty.
    /// Only a genuine store failure (not "the index is down") is returned as a <see cref="Result{TValue, TError}.Failure"/>.
    /// <c>MarkRecalledAsync</c> runs here, before the provider/tools apply the untrusted-content scanner, so
    /// <c>RecallCount</c>/<c>LastRecalledAt</c> may over-report memories that were then quarantined and never shown.
    /// </summary>
    ValueTask<Result<MemoryRecallResult, AgentError>> RecallAsync(string query, MemoryScope scope, RecallOptions options, CancellationToken ct);

    /// <summary>
    /// Archive (<paramref name="hard"/> = false) or delete a memory owned by <c>scope.OwnerId</c>; other owners → <see cref="AgentErrorCode.MemoryForbidden"/>.
    /// Both remove the vector from the index; a soft forget also sets <c>IndexPending</c>, so a record a host un-archives later
    /// (<c>UpdateAsync { IsArchived = false }</c>) is re-embedded by the next pending-only <see cref="ReindexAsync"/>.
    /// </summary>
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
