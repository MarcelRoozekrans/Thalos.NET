namespace Thalos.Skills;

/// <summary>Filter for <see cref="ISkillStore.ListAsync"/>. Null <em>and empty</em> filter lists mean "no filter".</summary>
public sealed record SkillQuery
{
    /// <summary>Only these names. Null/empty = every name.</summary>
    public IReadOnlyList<SkillName>? Names { get; init; }

    /// <summary>Every listed tag must be present. Query tags are normalised like stored tags (<see cref="SkillRules.NormalizeTags"/>) and matched ordinally.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Include skills whose file has disappeared (<see cref="SkillDocument.IsActive"/> false); default false.</summary>
    public bool IncludeInactive { get; init; }

    /// <summary>The filter semantics every store must implement.</summary>
    public bool Matches(SkillDocument skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        if (!IncludeInactive && !skill.IsActive)
        {
            return false;
        }

        if (Names is { Count: > 0 } && !Names.Contains(skill.Name))
        {
            return false;
        }

        if (Tags is { Count: > 0 })
        {
            foreach (var tag in Tags)
            {
                var normalized = SkillRules.NormalizeTag(tag);
                if (string.IsNullOrEmpty(normalized) || !skill.Tags.Contains(normalized, StringComparer.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
