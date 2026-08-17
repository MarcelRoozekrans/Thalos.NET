using ZeroAlloc.Results;

namespace Thalos.Memory;

/// <summary>
/// Vector side of memory: owns the embedding generator, stores one vector per record keyed by <see cref="MemoryId"/> and
/// searches within a <see cref="MemoryScope"/>. A rebuildable cache — the store is the source of truth. Contract:
/// <c>Thalos.Testing.MemoryIndexContractTests</c>.
/// </summary>
public interface IMemoryIndex
{
    /// <summary>
    /// Embeds and upserts (same id replaces; duplicate ids within one batch → the last one wins). Empty batch → success.
    /// Generator/backend down → <see cref="AgentErrorCode.MemoryIndexUnavailable"/>. On failure callers must assume none of the
    /// batch was written (it may have been partially written — backends need not be transactional; re-upserting is idempotent);
    /// the store's <see cref="MemoryRecord.IndexPending"/> flag stays authoritative.
    /// </summary>
    ValueTask<UnitResult<AgentError>> UpsertAsync(IReadOnlyList<MemoryRecord> records, CancellationToken ct);

    /// <summary>Hits visible in <paramref name="scope"/> (see <see cref="MemoryScope.Includes"/>) with score ≥ MinScore, best first, at most TopK (values ≤ 0 are treated as 1); each id at most once even when it is visible through several <see cref="MemoryScope.Partitions"/>. Blank query → empty.</summary>
    ValueTask<Result<IReadOnlyList<MemoryHit>, AgentError>> SearchAsync(string query, MemoryScope scope, MemorySearchOptions options, CancellationToken ct);

    /// <summary>Removes the vector; unknown id → success.</summary>
    ValueTask<UnitResult<AgentError>> RemoveAsync(MemoryId id, CancellationToken ct);

    /// <summary>Availability and (when known) vector dimensions. Returns a failure only for unexpected errors; "not available" is a successful <see cref="MemoryIndexHealth"/>.</summary>
    ValueTask<Result<MemoryIndexHealth, AgentError>> ProbeAsync(CancellationToken ct);
}
