namespace Thalos.Workspaces;

/// <summary>
/// Repository-relative paths a run may read but never write. An entry ending in <c>/</c> protects that directory and
/// everything under it; any other entry protects exactly that file. Case-insensitive, with <c>\</c> read as <c>/</c>,
/// and each segment compared without the trailing dots and spaces Windows drops from a name, so <c>.github./x.yml</c>
/// and <c>.github /x.yml</c> match <c>.github/</c>. Shared by the <c>workspace__*</c> tools, the sandbox host, and the
/// publish-side patch guard.
/// </summary>
public sealed class ProtectedPathSet
{
    private const char Backslash = (char)92;

    private readonly string[] _directories;
    private readonly HashSet<string> _files;

    /// <summary>Builds the set from <paramref name="entries"/>; each is canonicalised, and empty or whitespace entries are ignored.</summary>
    /// <exception cref="ArgumentException">
    /// An entry contains a <c>..</c> segment, or a segment of only dots and spaces that Windows could read as one.
    /// </exception>
    public ProtectedPathSet(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = new List<string>();
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry, nameof(entries));
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var isDirectory = entry.Replace(Backslash, '/').EndsWith('/');
            var normalized = Normalize(entry, out var escapes);
            if (escapes)
            {
                throw new ArgumentException(
                    $"A protected path entry must not contain a '..' segment, or a segment of only dots and spaces: '{entry}'.", nameof(entries));
            }

            if (normalized.Length == 0)
            {
                continue;
            }

            var canonical = isDirectory ? normalized + "/" : normalized;
            if (!list.Contains(canonical, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(canonical);
            }
        }

        Entries = list;
        _directories = [.. list.Where(e => e.EndsWith('/')).Select(e => e.TrimEnd('/'))];
        _files = new HashSet<string>(list.Where(e => !e.EndsWith('/')), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The normalised entries; directories keep their trailing <c>/</c>.</summary>
    public IReadOnlyList<string> Entries { get; }

    /// <summary>
    /// True when <paramref name="relativePath"/> is an entry, or lies under a directory entry. The path is canonicalised
    /// first: <c>\</c> becomes <c>/</c>, empty and <c>.</c> segments are dropped, and trailing dots and spaces are trimmed
    /// from every other segment. A path with a <c>..</c> segment, or a segment of only dots and spaces, is reported
    /// protected, failing closed, because it cannot be compared to an entry without resolving it.
    /// </summary>
    public bool IsProtected(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var path = Normalize(relativePath, out var escapes);
        if (escapes)
        {
            return true;
        }

        if (_files.Contains(path))
        {
            return true;
        }

        foreach (var directory in _directories)
        {
            if (string.Equals(path, directory, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Joins the non-empty, non-<c>.</c> segments with <c>/</c>, each without its trailing dots and spaces;
    /// <paramref name="hasParentSegment"/> reports a <c>..</c> segment, or another segment of only dots and spaces.
    /// </summary>
    private static string Normalize(string path, out bool hasParentSegment)
    {
        var segments = new List<string>();
        hasParentSegment = false;
        foreach (var segment in path.Replace(Backslash, '/').Split('/'))
        {
            if (segment.Length == 0 || string.Equals(segment, ".", StringComparison.Ordinal))
            {
                continue;
            }

            // Windows drops a name's trailing dots and spaces, so ".github." and ".github " open ".github". A segment
            // with nothing left, such as "..", "..." or " ", cannot be compared without resolving it.
            var trimmed = segment.TrimEnd('.', ' ');
            if (trimmed.Length == 0)
            {
                hasParentSegment = true;
                segments.Add(segment);
                continue;
            }

            segments.Add(trimmed);
        }

        return string.Join('/', segments);
    }
}
