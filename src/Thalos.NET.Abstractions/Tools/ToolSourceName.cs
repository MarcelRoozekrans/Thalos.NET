namespace Thalos;

/// <summary>
/// The rule for <see cref="IToolSource.Name"/>: non-empty, ASCII letters, digits, <c>_</c> and <c>-</c> only
/// (<c>^[a-zA-Z0-9_-]+$</c>) and never containing <c>__</c>, which is the source/tool separator in qualified
/// tool names (<c>{source}__{tool}</c>; Anthropic tool names must match <c>^[a-zA-Z0-9_-]{1,64}$</c>).
/// </summary>
public static class ToolSourceName
{
    /// <summary>Returns <see langword="true"/> when <paramref name="name"/> satisfies the source-name rule.</summary>
    public static bool IsValid(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var previousUnderscore = false;
        foreach (var c in name)
        {
            var underscore = c == '_';
            if (underscore && previousUnderscore)
            {
                return false;
            }

            if (!underscore && c != '-' && !char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }

            previousUnderscore = underscore;
        }

        return true;
    }

    /// <summary>Throws <see cref="ArgumentException"/> when <paramref name="name"/> violates the source-name rule.</summary>
    public static void ThrowIfInvalid(string? name, string paramName)
    {
        if (!IsValid(name))
        {
            throw new ArgumentException($"Tool source name '{name}' is invalid: it must match ^[a-zA-Z0-9_-]+$ and must not contain '__'.", paramName);
        }
    }
}
