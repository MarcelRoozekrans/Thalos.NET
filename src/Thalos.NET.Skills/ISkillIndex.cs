using ZeroAlloc.Results;

namespace Thalos.Skills;

/// <summary>An index hit.</summary>
/// <param name="Name">The skill that matched.</param>
/// <param name="Score">A cosine similarity in [0, 1]; higher is a better match.</param>
public readonly record struct SkillHit(SkillName Name, double Score);

/// <summary>
/// The search side of skills: one vector per skill, embedded from its <em>name, description and tags</em> — never its body,
/// because <c>skills__search</c> returns <c>name: description</c> lines and the agent decides what to load. A rebuildable
/// cache: the store is the source of truth and <see cref="SkillSyncService"/> refills the index on every start-up.
/// The contract is enforced by <c>Thalos.Testing.SkillIndexContractTests</c>.
/// </summary>
public interface ISkillIndex
{
    /// <summary>Embeds and upserts (same name replaces; duplicate names within one batch → the last wins). Empty batch → success.</summary>
    ValueTask<UnitResult<AgentError>> UpsertAsync(IReadOnlyList<SkillDocument> skills, CancellationToken ct);

    /// <summary>Hits with score ≥ <see cref="SkillSearchOptions.MinScore"/>, best first then by name, at most <see cref="SkillSearchOptions.TopK"/> (values ≤ 0 are treated as 1). A blank query returns an empty list; an unusable index returns <see cref="AgentErrorCode.SkillSearchUnavailable"/>.</summary>
    ValueTask<Result<IReadOnlyList<SkillHit>, AgentError>> SearchAsync(string query, SkillSearchOptions options, CancellationToken ct);

    /// <summary>Removes the vector; an unknown name is success.</summary>
    ValueTask<UnitResult<AgentError>> RemoveAsync(SkillName name, CancellationToken ct);

    /// <summary>The text an implementation embeds for <paramref name="skill"/>: name, description and tags, never the body.</summary>
    /// <param name="skill">The skill to describe.</param>
    /// <returns><c>"{name}: {description}"</c>, plus a line of space-separated tags when there are any.</returns>
    public static string EmbeddingText(SkillDocument skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        return skill.Tags.Count == 0
            ? $"{skill.Name.Value}: {skill.Description}"
            : $"{skill.Name.Value}: {skill.Description}\n{string.Join(' ', skill.Tags)}";
    }
}
