using System.Globalization;

namespace Thalos.Skills;

/// <summary>The limits every skill document must satisfy, and the tag normalisation stores and queries share.</summary>
/// <remarks>Deliberately a copy of the equivalent memory rules: Thalos.NET.Skills must not reference Thalos.NET.Memory (see the layering tests).</remarks>
public static partial class SkillRules
{
    private static readonly SkillDocumentValidator Validator = new(); // generated, stateless

    /// <summary>Returns null when <paramref name="skill"/> is valid, else a <see cref="AgentErrorCode.SkillValidationFailed"/> error naming the first violation.</summary>
    public static AgentError? Validate(SkillDocument skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        var result = Validator.Validate(skill);
        if (!result.IsValid)
        {
            var first = result.Failures[0];
            return AgentError.SkillValidationFailed($"{first.PropertyName}: {first.ErrorMessage}");
        }

        if (!SkillName.IsValid(skill.Name.Value))
        {
            return AgentError.SkillValidationFailed("Name must match ^[a-z][a-z0-9_-]{0,63}$.");
        }

        if (string.IsNullOrWhiteSpace(skill.Description))
        {
            return AgentError.SkillValidationFailed("Description is required.");
        }

        if (string.IsNullOrWhiteSpace(skill.Body))
        {
            return AgentError.SkillValidationFailed("Body is required.");
        }

        if (skill.Tags.Count > SkillDocument.MaxTags)
        {
            return AgentError.SkillValidationFailed(string.Create(CultureInfo.InvariantCulture, $"At most {SkillDocument.MaxTags} tags are allowed."));
        }

        foreach (var tag in skill.Tags)
        {
            if (string.IsNullOrWhiteSpace(tag) || tag.Length > SkillDocument.MaxTagLength)
            {
                return AgentError.SkillValidationFailed(string.Create(CultureInfo.InvariantCulture, $"Tags must be 1..{SkillDocument.MaxTagLength} characters."));
            }
        }

        return null;
    }

    /// <summary>Trims, lower-cases (invariant), drops blanks, removes ordinal duplicates, keeps order.</summary>
    public static IReadOnlyList<string> NormalizeTags(IEnumerable<string>? tags)
    {
        if (tags is null)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var raw in tags)
        {
            var tag = NormalizeTag(raw);
            if (!string.IsNullOrEmpty(tag) && seen.Add(tag))
            {
                list.Add(tag);
            }
        }

        return list;
    }

    /// <summary>Trims and lower-cases one tag (invariant); null in → null out. Does not check length or blankness.</summary>
    internal static string? NormalizeTag(string? tag)
    {
#pragma warning disable CA1308 // tags are lower-case identifiers by definition, not user-facing text
        return tag?.Trim().ToLowerInvariant();
#pragma warning restore CA1308
    }
}
