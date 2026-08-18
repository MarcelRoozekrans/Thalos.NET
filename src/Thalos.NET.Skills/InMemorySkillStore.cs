using System.Collections.Concurrent;
using ZeroAlloc.Results;

namespace Thalos.Skills;

/// <summary>Non-durable store for tests, samples and single-process hosts. <paramref name="clock"/> stamps deactivations.</summary>
/// <param name="clock">Supplies the <c>UpdatedAt</c> stamp written by <see cref="DeactivateMissingAsync"/>.</param>
public sealed class InMemorySkillStore(TimeProvider clock) : ISkillStore
{
    private readonly ConcurrentDictionary<SkillName, SkillDocument> _skills = new();
    private readonly object _gate = new(); // DeactivateMissing is a read-modify-write over the whole set

    /// <inheritdoc />
    public ValueTask<Result<SkillDocument, AgentError>> UpsertAsync(SkillDocument skill, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(skill);
        var stored = skill with { Tags = SkillRules.NormalizeTags(skill.Tags) };
        lock (_gate)
        {
            _skills[stored.Name] = stored;
        }

        return new(Result<SkillDocument, AgentError>.Success(stored));
    }

    /// <inheritdoc />
    public ValueTask<Result<SkillDocument, AgentError>> GetAsync(SkillName name, CancellationToken ct) =>
        new(_skills.TryGetValue(name, out var skill)
            ? Result<SkillDocument, AgentError>.Success(skill)
            : Result<SkillDocument, AgentError>.Failure(AgentError.SkillNotFound(name.Value)));

    /// <inheritdoc />
    public ValueTask<Result<IReadOnlyList<SkillDocument>, AgentError>> ListAsync(SkillQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        IReadOnlyList<SkillDocument> matches = _skills.Values.Where(query.Matches).OrderBy(s => s.Name).ToList();
        return new(Result<IReadOnlyList<SkillDocument>, AgentError>.Success(matches));
    }

    /// <inheritdoc />
    public ValueTask<UnitResult<AgentError>> DeactivateMissingAsync(IReadOnlyList<SkillName> seen, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seen);
        var keep = new HashSet<SkillName>();
        for (var i = 0; i < seen.Count; i++)
        {
            keep.Add(seen[i]);
        }

        var now = clock.GetUtcNow();
        lock (_gate)
        {
            foreach (var (name, skill) in _skills)
            {
                if (skill.IsActive && !keep.Contains(name))
                {
                    _skills[name] = skill with { IsActive = false, UpdatedAt = now };
                }
            }
        }

        return new(UnitResult<AgentError>.Success());
    }
}
