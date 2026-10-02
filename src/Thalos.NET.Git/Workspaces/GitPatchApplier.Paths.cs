using Thalos.Workspaces;

namespace Thalos.Git.Workspaces;

/// <summary>The path rules and git output parsers <see cref="GitPatchApplier"/> uses; see its remarks.</summary>
public sealed partial class GitPatchApplier
{
    private const char Backslash = (char)92;
    private const string SymlinkMode = "120000";
    private const string GitlinkMode = "160000";

    /// <summary>One entry of <c>git diff --raw -z --no-renames</c>: the new mode and the path.</summary>
    internal readonly record struct RawEntry(string NewMode, string Path);

    /// <summary>
    /// Why <paramref name="paths"/> must not be published, or <see langword="null"/> when nothing in it is refused. A
    /// path is refused when it is empty or holds a line break, leaves the worktree or names a <c>.git</c> directory, is
    /// protected, ends in <c>.gitattributes</c> or <c>.gitmodules</c> at any depth, has a Windows short-name segment,
    /// or lies beyond one of <paramref name="links"/>.
    /// </summary>
    /// <param name="paths">Repository-relative paths, as git prints them.</param>
    /// <param name="protectedPaths">The configured protected paths.</param>
    /// <param name="links">The symlinks and submodules the index already holds, or <see langword="null"/> for none.</param>
    internal static string? FindRefusal(IEnumerable<string> paths, ProtectedPathSet protectedPaths, IReadOnlySet<string>? links = null)
    {
        foreach (var path in paths)
        {
            var refusal = FindPathRefusal(path, protectedPaths, links);
            if (refusal is not null)
            {
                return refusal;
            }
        }

        return null;
    }

    private static string? FindPathRefusal(string path, ProtectedPathSet protectedPaths, IReadOnlySet<string>? links)
    {
        if (path.Length == 0 || path.Contains('\n', StringComparison.Ordinal) || path.Contains('\r', StringComparison.Ordinal))
        {
            return "the change touches a path that is empty or holds a line break; publish refused";
        }

        if (LeavesWorktreeOrNamesGitDirectory(path))
        {
            return $"the change touches path '{path}', which leaves the worktree or names a .git directory; publish refused";
        }

        if (protectedPaths.IsProtected(path) || IsAttributesOrModulesFile(path))
        {
            return $"the change touches protected path '{path}'; publish refused";
        }

        if (HasShortNameSegment(path))
        {
            return $"the change touches path '{path}', which can name another file on Windows; publish refused";
        }

        return links is not null && LiesBeyond(path, links)
            ? $"the change touches path '{path}', which lies beyond a symlink or submodule; publish refused"
            : null;
    }

    /// <summary>
    /// A leading <c>/</c> or <c>\</c>, a <c>:</c> (a drive prefix or an NTFS stream), a <c>..</c> segment, or a
    /// <c>.git</c> segment at any depth, compared case-insensitively and ignoring the trailing dots and spaces Windows
    /// drops from a name.
    /// </summary>
    internal static bool LeavesWorktreeOrNamesGitDirectory(string path)
    {
        if (path[0] is '/' or Backslash || path.Contains(':', StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var segment in path.Split('/', Backslash))
        {
            if (string.Equals(segment, "..", StringComparison.Ordinal)
                || string.Equals(segment.TrimEnd('.', ' '), ".git", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The last segment is <c>.gitattributes</c> or <c>.gitmodules</c>, at any depth, case-insensitively.</summary>
    internal static bool IsAttributesOrModulesFile(string path)
    {
        var name = path[(path.LastIndexOfAny(['/', Backslash]) + 1)..].TrimEnd('.', ' ');
        return string.Equals(name, ".gitattributes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, ".gitmodules", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <see langword="true"/> when a segment of <paramref name="path"/> is shaped like a Windows 8.3 short name: a
    /// <c>~</c> followed by one or more digits, then the end of the path, a <c>.</c>, a <c>/</c> or a <c>\</c> —
    /// <c>GITHUB~1</c>, <c>GITATT~1.TXT</c>.
    /// </summary>
    internal static bool HasShortNameSegment(string path)
    {
        for (var tilde = path.IndexOf('~', StringComparison.Ordinal); tilde >= 0; tilde = path.IndexOf('~', tilde + 1))
        {
            var end = tilde + 1;
            while (end < path.Length && char.IsAsciiDigit(path[end]))
            {
                end++;
            }

            if (end > tilde + 1 && (end == path.Length || path[end] is '.' or '/' or Backslash))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A proper prefix of <paramref name="path"/> is one of <paramref name="links"/>.</summary>
    private static bool LiesBeyond(string path, IReadOnlySet<string> links)
    {
        for (var slash = path.IndexOf('/', StringComparison.Ordinal); slash > 0; slash = path.IndexOf('/', slash + 1))
        {
            if (links.Contains(path[..slash]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Why an entry in <paramref name="entries"/> must not be published because of its new mode, or <see langword="null"/>.</summary>
    private static string? FindLinkModeRefusal(IEnumerable<RawEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.NewMode is SymlinkMode or GitlinkMode)
            {
                return $"the change makes '{entry.Path}' a symlink or submodule; publish refused";
            }
        }

        return null;
    }

    /// <summary>
    /// The symlink and submodule paths in <c>git ls-files -s -z</c> output, each record <c>mode sha stage\tpath</c>,
    /// compared case-insensitively so a case variant on a case-insensitive filesystem still matches.
    /// </summary>
    private static HashSet<string> LinkPaths(string output)
    {
        var links = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in SplitNul(output))
        {
            var tab = record.IndexOf('\t', StringComparison.Ordinal);
            if (tab > 0 && (record.StartsWith(SymlinkMode, StringComparison.Ordinal) || record.StartsWith(GitlinkMode, StringComparison.Ordinal)))
            {
                links.Add(record[(tab + 1)..]);
            }
        }

        return links;
    }

    /// <summary>
    /// The entries in <c>git diff --raw -z --no-renames</c> output: a header <c>:oldmode newmode oldsha newsha status</c>
    /// then the path, each NUL-terminated. <see langword="null"/> when the output is malformed.
    /// </summary>
    internal static List<RawEntry>? ParseRaw(string output)
    {
        var fields = SplitNul(output);
        var entries = new List<RawEntry>();
        for (var i = 0; i < fields.Count; i += 2)
        {
            var header = fields[i].Split(' ');
            if (header.Length < 5 || !header[0].StartsWith(':') || i + 1 >= fields.Count)
            {
                return null;
            }

            entries.Add(new RawEntry(header[1], fields[i + 1]));
        }

        return entries;
    }

    /// <summary>
    /// The names in <c>git apply --numstat -z</c> output: <c>added\tdeleted\tname\0</c> per file, or
    /// <c>added\tdeleted\t\0old\0new\0</c> should git ever print both sides of a rename the way <c>git diff</c> does.
    /// Binary files print <c>-</c> for both counts. <see langword="null"/> when a record is malformed.
    /// </summary>
    internal static List<string>? ParseNumstat(string output)
    {
        var fields = SplitNul(output);
        var names = new List<string>();
        for (var i = 0; i < fields.Count; i++)
        {
            var record = fields[i];
            var first = record.IndexOf('\t', StringComparison.Ordinal);
            var second = first < 0 ? -1 : record.IndexOf('\t', first + 1);
            if (second < 0 || !IsCount(record.AsSpan(0, first)) || !IsCount(record.AsSpan(first + 1, second - first - 1)))
            {
                return null;
            }

            var name = record[(second + 1)..];
            if (name.Length > 0)
            {
                names.Add(name);
                continue;
            }

            if (i + 2 >= fields.Count)
            {
                return null;
            }

            names.Add(fields[++i]);
            names.Add(fields[++i]);
        }

        return names;
    }

    /// <summary>The paths in <c>git status --porcelain -z --no-renames</c> output, each record <c>XY path</c>.</summary>
    private static IEnumerable<string> StatusNames(IReadOnlyList<string> records) =>
        records.Select(record => record.Length > 3 ? record[3..] : string.Empty);

    /// <summary>
    /// Splits NUL-terminated output into its fields. <see cref="GitCli"/> appends a line feed after the last line it
    /// reads, so one trailing line feed is dropped first; an empty last field after the final NUL is not a field.
    /// </summary>
    private static List<string> SplitNul(string output)
    {
        var text = output.EndsWith('\n') ? output[..^1] : output;
        var fields = new List<string>(text.Split('\0'));
        if (fields.Count > 0 && fields[^1].Length == 0)
        {
            fields.RemoveAt(fields.Count - 1);
        }

        return fields;
    }

    private static bool IsCount(ReadOnlySpan<char> value)
    {
        if (value is "-")
        {
            return true;
        }

        if (value.IsEmpty)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFullSha(string? value)
    {
        if (value is not { Length: 40 })
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
