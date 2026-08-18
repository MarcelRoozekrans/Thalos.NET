using ZeroAlloc.Results;
using ZeroAlloc.Telemetry;

namespace Thalos.Skills;

/// <summary>
/// Persistence for skill documents (no vectors). Implementations must be safe for concurrent use. Tags are persisted
/// normalised (<see cref="SkillRules.NormalizeTags"/>) by <see cref="UpsertAsync"/>, so reads always return the canonical
/// form. The store is written only by <c>SkillSyncService</c> — files are the source of truth and no agent may write here.
/// The contract is enforced by <c>Thalos.Testing.SkillStoreContractTests</c>.
/// </summary>
[Instrument("thalos", PublicProxy = true)]
public interface ISkillStore
{
    /// <summary>Inserts or replaces the skill with <paramref name="skill"/>'s name, as given (timestamp included; tags normalised). Returns the stored document.</summary>
    [Trace("thalos.skills.upsert")]
    ValueTask<Result<SkillDocument, AgentError>> UpsertAsync(SkillDocument skill, CancellationToken ct);

    /// <summary>Unknown name → <see cref="AgentErrorCode.SkillNotFound"/>. Inactive skills are returned (callers decide).</summary>
    [Trace("thalos.skills.get")]
    ValueTask<Result<SkillDocument, AgentError>> GetAsync(SkillName name, CancellationToken ct);

    /// <summary>Every match of <paramref name="query"/> (see <see cref="SkillQuery.Matches"/>), ordered by <see cref="SkillDocument.Name"/> ascending (ordinal). No paging: a skill library is a folder of files.</summary>
    [Trace("thalos.skills.list")]
    ValueTask<Result<IReadOnlyList<SkillDocument>, AgentError>> ListAsync(SkillQuery query, CancellationToken ct);

    /// <summary>
    /// Sets <see cref="SkillDocument.IsActive"/> false and stamps <c>UpdatedAt</c> for every currently active skill whose name is
    /// <em>not</em> in <paramref name="seen"/>; already-inactive rows are untouched. <paramref name="seen"/> is a set (duplicates
    /// count once). An empty list deactivates everything — the caller decides whether that is meant (<c>SkillSyncService</c>
    /// refuses to when every root was unreadable).
    /// </summary>
    [Trace("thalos.skills.deactivate-missing")]
    ValueTask<UnitResult<AgentError>> DeactivateMissingAsync(IReadOnlyList<SkillName> seen, CancellationToken ct);
}
