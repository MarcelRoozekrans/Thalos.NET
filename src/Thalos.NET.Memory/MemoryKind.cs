using System.Diagnostics.CodeAnalysis;

namespace Thalos.Memory;

/// <summary>Category of a memory. Built-in kinds are lowercase identifiers; hosts may define more (<c>^[a-z][a-z0-9_-]{0,31}$</c>).</summary>
public sealed record MemoryKind(string Value)
{
    public const int MaxLength = 32;

    public static readonly MemoryKind Fact = new("fact");
    public static readonly MemoryKind Preference = new("preference");
    public static readonly MemoryKind Decision = new("decision");
    public static readonly MemoryKind Learning = new("learning");
    public static readonly MemoryKind Note = new("note");

    /// <summary>Returns <see langword="true"/> when <paramref name="value"/> is a valid (already normalised) kind identifier.</summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength || !char.IsAsciiLetterLower(value[0]))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c is not ('_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Trims and lower-cases <paramref name="value"/>; succeeds when the result satisfies <see cref="IsValid"/>.</summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out MemoryKind? kind)
    {
#pragma warning disable CA1308 // kinds are lowercase identifiers by definition, not user-facing text
        var normalized = value?.Trim().ToLowerInvariant();
#pragma warning restore CA1308
        if (IsValid(normalized))
        {
            kind = new MemoryKind(normalized!);
            return true;
        }

        kind = null;
        return false;
    }

    public override string ToString() => Value;
}
