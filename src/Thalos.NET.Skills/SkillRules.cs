namespace Thalos.Skills;

/// <summary>The limits every skill document must satisfy, and the tag normalisation stores and queries share.</summary>
/// <remarks>Deliberately a copy of the equivalent memory rules: Thalos.NET.Skills must not reference Thalos.NET.Memory (see the layering tests).</remarks>
public static partial class SkillRules
{
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
