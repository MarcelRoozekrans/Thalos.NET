using ZeroAlloc.Results;

namespace Thalos.Memory;

/// <summary>Facade over <see cref="IMemoryStore"/> + <see cref="IMemoryIndex"/>: the only entry point tools, providers and host code use.</summary>
public interface IMemoryService
{
    /// <summary>
    /// Validate (owner must be a non-blank, non-anonymous id) → dedupe (same owner, ≥ threshold refreshes the existing record) → create → index.
    /// An index failure leaves the record with <c>IndexPending</c> and still returns success.
    /// </summary>
    ValueTask<Result<MemoryRecord, AgentError>> RememberAsync(RememberRequest request, CancellationToken ct);

    /// <summary>Search within <paramref name="scope"/>, hydrate, drop archived/missing, order by score ↓ importance ↓ UpdatedAt ↓, apply TopK/MaxChars, mark recalled. Index failures are returned (callers decide); blank query → empty.</summary>
    ValueTask<Result<IReadOnlyList<RecalledMemory>, AgentError>> RecallAsync(string query, MemoryScope scope, RecallOptions options, CancellationToken ct);

    /// <summary>Archive (<paramref name="hard"/> = false) or delete a memory owned by <c>scope.OwnerId</c>; other owners → <see cref="AgentErrorCode.MemoryForbidden"/>.</summary>
    ValueTask<UnitResult<AgentError>> ForgetAsync(MemoryId id, MemoryScope scope, bool hard, CancellationToken ct);

    /// <summary>Paged listing; <see cref="MemoryQuery.OwnerIds"/> must contain at least one owner.</summary>
    ValueTask<Result<MemoryPage, AgentError>> ListAsync(MemoryQuery query, CancellationToken ct);

    /// <summary>Re-embeds pending (or all non-archived) records in batches and clears <c>IndexPending</c>; fails fast when the index probe says unavailable.</summary>
    ValueTask<Result<ReindexReport, AgentError>> ReindexAsync(ReindexOptions options, CancellationToken ct);
}
