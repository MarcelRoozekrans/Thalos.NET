using ZeroAlloc.Results;

namespace Thalos.Memory;

/// <summary>
/// Vector side of memory: owns the embedding generator, stores one vector per record keyed by <see cref="MemoryId"/> and
/// searches within a <see cref="MemoryScope"/>. A rebuildable cache — the store is the source of truth. Contract:
/// <c>Thalos.Testing.MemoryIndexContractTests</c>.
/// </summary>
public interface IMemoryIndex
{
    /// <summary>Embeds and upserts (same id replaces). Empty batch → success. Generator/backend down → <see cref="AgentErrorCode.MemoryIndexUnavailable"/>.</summary>
    ValueTask<UnitResult<AgentError>> UpsertAsync(IReadOnlyList<MemoryRecord> records, CancellationToken ct);

    /// <summary>Hits visible in <paramref name="scope"/> (see <see cref="MemoryScope.Includes"/>) with score ≥ MinScore, best first, at most TopK. Blank query → empty.</summary>
    ValueTask<Result<IReadOnlyList<MemoryHit>, AgentError>> SearchAsync(string query, MemoryScope scope, MemorySearchOptions options, CancellationToken ct);

    /// <summary>Removes the vector; unknown id → success.</summary>
    ValueTask<UnitResult<AgentError>> RemoveAsync(MemoryId id, CancellationToken ct);

    /// <summary>Availability and (when known) vector dimensions. Returns a failure only for unexpected errors; "not available" is a successful <see cref="MemoryIndexHealth"/>.</summary>
    ValueTask<Result<MemoryIndexHealth, AgentError>> ProbeAsync(CancellationToken ct);
}
