using System.Globalization;

namespace Thalos.Memory;

/// <summary>The limits every memory must satisfy (text ≤ 4000, ≤ 10 tags of ≤ 32 chars, source ≤ 256, importance 0..1, valid kind, non-empty owner).</summary>
public static class MemoryRules
{
    private static readonly MemoryRecordValidator Validator = new(); // generated, stateless

    /// <summary>Returns null when <paramref name="record"/> is valid, else a <see cref="AgentErrorCode.MemoryValidationFailed"/> error naming the first violation.</summary>
    public static AgentError? Validate(MemoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var result = Validator.Validate(record);
        if (!result.IsValid)
        {
            var first = result.Failures[0];
            return AgentError.MemoryValidationFailed($"{first.PropertyName}: {first.ErrorMessage}");
        }

        if (string.IsNullOrWhiteSpace(record.Text))
        {
            return AgentError.MemoryValidationFailed("Text is required.");
        }

        if (!MemoryKind.IsValid(record.Kind.Value))
        {
            return AgentError.MemoryValidationFailed("Kind must match ^[a-z][a-z0-9_-]{0,31}$.");
        }

        if (record.Tags.Count > MemoryRecord.MaxTags)
        {
            return AgentError.MemoryValidationFailed(string.Create(CultureInfo.InvariantCulture, $"At most {MemoryRecord.MaxTags} tags are allowed."));
        }

        foreach (var tag in record.Tags)
        {
            if (string.IsNullOrWhiteSpace(tag) || tag.Length > MemoryRecord.MaxTagLength)
            {
                return AgentError.MemoryValidationFailed(string.Create(CultureInfo.InvariantCulture, $"Tags must be 1..{MemoryRecord.MaxTagLength} characters."));
            }
        }

        if (record.Source.Length > MemoryRecord.MaxSourceLength)
        {
            return AgentError.MemoryValidationFailed(string.Create(CultureInfo.InvariantCulture, $"Source must be at most {MemoryRecord.MaxSourceLength} characters."));
        }

        if (double.IsNaN(record.Importance) || record.Importance is < 0 or > 1)
        {
            return AgentError.MemoryValidationFailed("Importance must be between 0 and 1.");
        }

        return null;
    }

    /// <summary>
    /// Trims, lower-cases (invariant), drops blanks, removes duplicates (ordinal), keeps order. Tags are stored lower-case and
    /// matched ordinally after this normalisation (<see cref="MemoryQuery.Matches"/> applies it to the query's tags too).
    /// </summary>
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
#pragma warning disable CA1308 // tags are lowercase identifiers by definition, not user-facing text
        return tag?.Trim().ToLowerInvariant();
#pragma warning restore CA1308
    }
}
