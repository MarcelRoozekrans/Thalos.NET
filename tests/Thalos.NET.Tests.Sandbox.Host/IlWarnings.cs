using System.Text.RegularExpressions;

namespace Thalos.Tests.Sandbox.Host;

/// <summary>
/// Counts a publish log's trim and AOT warnings, IL2xxx and IL3xxx, as Daedalus's <c>eng/aot/count-warnings.sh</c> does,
/// so the smoke test's zero is the number that script would report.
/// </summary>
/// <remarks>
/// A key is <c>code|file|line|member</c>. The file is relative to the repository and the line drops the column, so
/// ILC's <c>File.cs(5)</c> and the analyzer's <c>File.cs(5,27)</c> report of one site are one warning. With a location,
/// the member is the message, with ILC's leading <c>Origin(): </c> removed, since the analyzer prints none. Without a
/// location, as ILC reports package code (<c>ILC : Trim analysis warning IL2026: Origin(): message</c>), the key keeps
/// <c>Origin(): message</c>, because only the used member tells two uses in one method apart. MSBuild's end-of-build
/// repeat of a line is the same key.
/// </remarks>
internal static partial class IlWarnings
{
    /// <summary>The distinct warning keys of <paramref name="output"/>, in ordinal order.</summary>
    /// <param name="output">The publish's console output.</param>
    /// <param name="repoRoot">The repository root, removed from the start of each file.</param>
    public static IReadOnlyList<string> Distinct(string output, string repoRoot)
    {
        var prefixOfRepo = repoRoot.EndsWith('/') ? repoRoot : repoRoot + "/";
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var line = ProjectSuffix().Replace(raw.TrimEnd('\r'), "");
            var warning = Warning().Match(line);
            if (!warning.Success)
            {
                continue;
            }

            var code = warning.Groups["code"].Value;
            var prefix = line[..warning.Index];
            var rest = line[(warning.Index + warning.Length)..];
            var analysis = Analysis().IsMatch(prefix);
            prefix = Analysis().Replace(prefix, "");
            prefix = TrailingColon().Replace(prefix, "").TrimStart(' ');

            var file = "";
            var lineNumber = "";
            if (prefix is not ("ILC" or "ILLink" or ""))
            {
                var location = Location().Match(prefix);
                file = location.Success ? prefix[..location.Index] : prefix;
                lineNumber = location.Success ? location.Groups["line"].Value : "";
                if (file.StartsWith(prefixOfRepo, StringComparison.Ordinal))
                {
                    file = file[prefixOfRepo.Length..];
                }
            }

            var message = rest;
            var originEnd = rest.IndexOf(": ", StringComparison.Ordinal);
            if (analysis && originEnd > 0)
            {
                message = rest[(originEnd + 2)..];
            }

            keys.Add(file.Length > 0 ? $"{code}|{file}|{lineNumber}|{message}" : $"{code}|||{rest}");
        }

        return [.. keys];
    }

    [GeneratedRegex(@"warning (?<code>IL[23][0-9]{3}): ", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Warning();

    /// <summary>MSBuild's <c> [project]</c> suffix.</summary>
    [GeneratedRegex(@" \[[^\[\]]*\]$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ProjectSuffix();

    [GeneratedRegex(@"(?:Trim|AOT) analysis $", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Analysis();

    [GeneratedRegex(@" *: *$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TrailingColon();

    [GeneratedRegex(@"\((?<line>[0-9]+)(?:,[0-9]+)*\)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Location();
}
