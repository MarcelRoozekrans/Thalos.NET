using ZeroAlloc.Results;
using ZeroAlloc.Telemetry;

namespace Thalos.Memory;

/// <summary>
/// Persistence for memory records (no vectors). Implementations must be safe for concurrent use; updates and
/// <see cref="MarkRecalledAsync"/> are read-modify-write and must not lose concurrent writes (atomic UPDATE … SET n = n + 1).
/// Tags are persisted normalised (<see cref="MemoryRules.NormalizeTags"/>: trimmed, lower-cased, de-duplicated) by
/// <see cref="CreateAsync"/> and <see cref="UpdateAsync"/>, so reads always return the canonical form.
/// The contract is enforced by <c>Thalos.Testing.MemoryStoreContractTests</c>.
/// </summary>
[Instrument("thalos", PublicProxy = true)]
public interface IMemoryStore
{
    /// <summary>Inserts a new record as given (timestamps included; tags normalised). Duplicate id → <see cref="AgentErrorCode.MemoryStoreFailed"/>. Returns the stored record.</summary>
    [Trace("thalos.memory.create")]
    ValueTask<Result<MemoryRecord, AgentError>> CreateAsync(MemoryRecord record, CancellationToken ct);

    /// <summary>Unknown id → <see cref="AgentErrorCode.MemoryNotFound"/>. Archived records are returned (callers decide).</summary>
    [Trace("thalos.memory.get")]
    ValueTask<Result<MemoryRecord, AgentError>> GetAsync(MemoryId id, CancellationToken ct);

    /// <summary>Applies the non-null members of <paramref name="update"/> (tags normalised); bumps <c>UpdatedAt</c> only for content changes (<see cref="MemoryUpdate.TouchesContent"/>). Returns the updated record.</summary>
    [Trace("thalos.memory.update")]
    ValueTask<Result<MemoryRecord, AgentError>> UpdateAsync(MemoryId id, MemoryUpdate update, CancellationToken ct);

    /// <summary>Hard delete. Unknown id → <see cref="AgentErrorCode.MemoryNotFound"/>.</summary>
    [Trace("thalos.memory.delete")]
    ValueTask<UnitResult<AgentError>> DeleteAsync(MemoryId id, CancellationToken ct);

    /// <summary>Filters with <see cref="MemoryQuery.Matches"/>, orders by <c>UpdatedAt</c> desc then id desc, pages (page ≥ 1, size clamped to 1..100), returns the total match count.</summary>
    [Trace("thalos.memory.list")]
    ValueTask<Result<MemoryPage, AgentError>> ListAsync(MemoryQuery query, CancellationToken ct);

    /// <summary>Increments <c>RecallCount</c> and sets <c>LastRecalledAt = at</c> for every known id; unknown ids are ignored; empty list is a no-op.</summary>
    [Trace("thalos.memory.mark-recalled")]
    ValueTask<UnitResult<AgentError>> MarkRecalledAsync(IReadOnlyList<MemoryId> ids, DateTimeOffset at, CancellationToken ct);

    /// <summary>Streams every match of <paramref name="query"/> (paging ignored) oldest first — used by reindex.</summary>
    IAsyncEnumerable<MemoryRecord> StreamAsync(MemoryQuery query, CancellationToken ct);
}
