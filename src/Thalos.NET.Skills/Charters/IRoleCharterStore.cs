using ZeroAlloc.Results;

namespace Thalos.Skills.Charters;

/// <summary>
/// Persistence for role charters (no vectors). Implementations must be safe for concurrent use. The store is written
/// only by a sync — files are the source of truth and no agent may write here. The contract is enforced by
/// <c>Thalos.Testing.RoleCharterStoreContractTests</c>.
/// </summary>
public interface IRoleCharterStore
{
    /// <summary>Inserts or replaces the charter with <paramref name="charter"/>'s role and content hash, as given. Returns the stored charter.</summary>
    ValueTask<Result<RoleCharter, AgentError>> UpsertAsync(RoleCharter charter, CancellationToken ct);

    /// <summary>Every version of every role ever upserted; <see cref="RoleCharter.IsActive"/> reflects the role's current row.</summary>
    ValueTask<Result<IReadOnlyList<RoleCharter>, AgentError>> ListVersionsAsync(CancellationToken ct);

    /// <summary>
    /// Deactivates every role that is <em>not</em> in <paramref name="seenRoles"/> — its versions are kept, only
    /// <see cref="RoleCharter.IsActive"/> flips false. <paramref name="seenRoles"/> is a set (duplicates count once).
    /// </summary>
    ValueTask<UnitResult<AgentError>> DeactivateMissingAsync(IReadOnlyList<string> seenRoles, CancellationToken ct);
}
