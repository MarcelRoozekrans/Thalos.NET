namespace Thalos.Workspaces;

/// <summary>
/// Repository-relative paths a run may read but never write. An entry ending in <c>/</c> protects that directory and
/// everything under it; any other entry protects exactly that file. Case-insensitive, with <c>\</c> read as <c>/</c>.
/// Shared by the <c>workspace__*</c> tools, the sandbox host, and the publish-side patch guard.
/// </summary>
public sealed class ProtectedPathSet
{
    private readonly string[] _directories;
    private readonly HashSet<string> _files;

    public ProtectedPathSet(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = entries.Select(Normalize).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Entries = list;
        _directories = [.. list.Where(e => e.EndsWith('/')).Select(e => e.TrimEnd('/'))];
        _files = new HashSet<string>(list.Where(e => !e.EndsWith('/')), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The normalised entries; directories keep their trailing <c>/</c>.</summary>
    public IReadOnlyList<string> Entries { get; }

    public bool IsProtected(string relativePath)
    {
        var path = Normalize(relativePath).TrimEnd('/');
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

    private static string Normalize(string path) =>
        (path ?? throw new ArgumentNullException(nameof(path))).Replace('\\', '/').TrimStart('/').Replace("//", "/", StringComparison.Ordinal);
}
