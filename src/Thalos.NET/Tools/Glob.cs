namespace Thalos.Tools;

/// <summary>Minimal ordinal glob: <c>*</c> = any run, <c>?</c> = one char. No character classes.</summary>
public static class Glob
{
    public static bool IsMatch(string pattern, string input) => IsMatch(pattern.AsSpan(), input.AsSpan());

    private static bool IsMatch(ReadOnlySpan<char> p, ReadOnlySpan<char> s)
    {
        while (true)
        {
            if (p.IsEmpty)
            {
                return s.IsEmpty;
            }

            if (p[0] == '*')
            {
                p = p[1..];
                if (p.IsEmpty)
                {
                    return true;
                }

                for (var i = 0; i <= s.Length; i++)
                {
                    if (IsMatch(p, s[i..]))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (s.IsEmpty || (p[0] != '?' && p[0] != s[0]))
            {
                return false;
            }

            p = p[1..];
            s = s[1..];
        }
    }
}
