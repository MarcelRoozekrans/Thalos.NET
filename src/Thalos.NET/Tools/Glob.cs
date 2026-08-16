namespace Thalos.Tools;

/// <summary>Minimal ordinal glob: <c>*</c> = any run (including empty), <c>?</c> = exactly one char. No character classes.</summary>
/// <remarks>
/// Linear two-pointer greedy matcher (O(pattern × input) worst case, no recursion, no allocation): the last
/// <c>*</c> position and the input index it was matched at are remembered so a mismatch backtracks to that star
/// and lets it swallow one more character.
/// </remarks>
public static class Glob
{
    /// <summary>Returns true when <paramref name="input"/> matches <paramref name="pattern"/> (ordinal, case-sensitive).</summary>
    public static bool IsMatch(string pattern, string input)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(input);
        return IsMatch(pattern.AsSpan(), input.AsSpan());
    }

    private static bool IsMatch(ReadOnlySpan<char> p, ReadOnlySpan<char> s)
    {
        var pi = 0;
        var si = 0;
        var starP = -1; // index of the last '*' seen in p, or -1
        var starS = 0;  // input index the last '*' currently spans up to

        while (si < s.Length)
        {
            if (pi < p.Length && p[pi] == '*')
            {
                starP = pi;
                starS = si;
                pi++;
            }
            else if (pi < p.Length && (p[pi] == '?' || p[pi] == s[si]))
            {
                pi++;
                si++;
            }
            else if (starP >= 0)
            {
                // Mismatch: let the last '*' absorb one more input char and retry after it.
                pi = starP + 1;
                starS++;
                si = starS;
            }
            else
            {
                return false;
            }
        }

        // Only trailing stars may remain unmatched in the pattern.
        while (pi < p.Length && p[pi] == '*')
        {
            pi++;
        }

        return pi == p.Length;
    }
}
