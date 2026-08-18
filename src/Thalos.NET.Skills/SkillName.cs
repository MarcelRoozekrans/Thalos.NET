using System.Diagnostics.CodeAnalysis;

namespace Thalos.Skills;

/// <summary>
/// The identity of a skill: a lower-case identifier matching <c>^[a-z][a-z0-9_-]{0,63}$</c> that also names the file or folder the
/// skill was loaded from. Not a generated id — it is authored in git — so it is validated, never minted. <see langword="default"/>
/// carries an empty <see cref="Value"/> and never equals a parsed name.
/// </summary>
public readonly record struct SkillName : IComparable<SkillName>, IParsable<SkillName>
{
    /// <summary>Maximum length of a skill name.</summary>
    public const int MaxLength = 64;

    private readonly string? _value;

    private SkillName(string value) => _value = value;

    /// <summary>The identifier, or an empty string for <see langword="default"/>.</summary>
    public string Value => _value ?? "";

    /// <summary>Returns <see langword="true"/> when <paramref name="value"/> is an already-normalised valid skill name.</summary>
    public static bool IsValid([NotNullWhen(true)] string? value)
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

    /// <summary>Trims and lower-cases <paramref name="value"/> (invariant); succeeds when the result satisfies <see cref="IsValid"/>.</summary>
    public static bool TryParse(string? value, out SkillName name)
    {
#pragma warning disable CA1308 // skill names are lower-case identifiers by definition, not user-facing text
        var normalized = value?.Trim().ToLowerInvariant();
#pragma warning restore CA1308
        if (IsValid(normalized))
        {
            name = new SkillName(normalized);
            return true;
        }

        name = default;
        return false;
    }

    /// <inheritdoc />
    public static bool TryParse(string? s, IFormatProvider? provider, out SkillName result) => TryParse(s, out result);

    /// <summary>Parses <paramref name="s"/> as a skill name.</summary>
    /// <exception cref="FormatException"><paramref name="s"/> is not a valid skill name.</exception>
    /// <remarks>
    /// The <see cref="IParsable{TSelf}"/> overload is implemented explicitly: a public <c>Parse(string, IFormatProvider?)</c> would make
    /// every call site trip CA1305, and a skill name is a culture-invariant ASCII identifier that no format provider can influence.
    /// </remarks>
    public static SkillName Parse(string s) =>
        TryParse(s, out var name) ? name : throw new FormatException("Value is not a valid skill name (^[a-z][a-z0-9_-]{0,63}$).");

    /// <inheritdoc />
    static SkillName IParsable<SkillName>.Parse(string s, IFormatProvider? provider) => Parse(s);

    /// <summary>Ordinal comparison of <see cref="Value"/>.</summary>
    public int CompareTo(SkillName other) => string.CompareOrdinal(Value, other.Value);

    /// <summary>Ordinal less-than.</summary>
    public static bool operator <(SkillName left, SkillName right) => left.CompareTo(right) < 0;

    /// <summary>Ordinal greater-than.</summary>
    public static bool operator >(SkillName left, SkillName right) => left.CompareTo(right) > 0;

    /// <summary>Ordinal less-than-or-equal.</summary>
    public static bool operator <=(SkillName left, SkillName right) => left.CompareTo(right) <= 0;

    /// <summary>Ordinal greater-than-or-equal.</summary>
    public static bool operator >=(SkillName left, SkillName right) => left.CompareTo(right) >= 0;

    /// <summary>The identifier.</summary>
    public override string ToString() => Value;
}
