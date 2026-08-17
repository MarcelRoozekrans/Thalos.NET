using System.Diagnostics.CodeAnalysis;

namespace Thalos.Memory;

/// <summary>Category of a memory. Built-in kinds are lowercase identifiers; hosts may define more (<c>^[a-z][a-z0-9_-]{0,31}$</c>).</summary>
public sealed record MemoryKind(string Value)
{
    /// <summary>Maximum length of a kind identifier.</summary>
    public const int MaxLength = 32;

    /// <summary><c>fact</c>: something true about the user or the work.</summary>
    public static readonly MemoryKind Fact = new("fact");

    /// <summary><c>preference</c>: how the user likes things done.</summary>
    public static readonly MemoryKind Preference = new("preference");

    /// <summary><c>decision</c>: a choice that was made and should be respected later.</summary>
    public static readonly MemoryKind Decision = new("decision");

    /// <summary><c>learning</c>: something discovered while working (e.g. a Ralph learning).</summary>
    public static readonly MemoryKind Learning = new("learning");

    /// <summary><c>note</c>: anything else; the default kind.</summary>
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

    /// <summary>The kind identifier.</summary>
    public override string ToString() => Value;
}
