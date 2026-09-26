namespace Thalos.Workspaces;

/// <summary>Options for <see cref="WorkspaceTools"/>, the <c>workspace__*</c> file tools.</summary>
public sealed class RunWorkspaceToolOptions
{
    /// <summary>The tool source name the tools are registered under: <c>workspace__read_file</c>, etc.</summary>
    public const string SourceName = "workspace";

    /// <summary>
    /// The host-wide ceiling of writable file extensions, each with its leading dot (e.g. <c>".cs"</c>), compared
    /// case-insensitively. Required, with no default: absence is not a supported configuration (rulings R27 and
    /// R29). An empty set refuses every write. A caller carrying the <see cref="RunWorkspaceClaims.WriteExtensions"/>
    /// claim is narrowed further, to the extensions in both this ceiling and the caller's own grant.
    /// </summary>
    public required IReadOnlySet<string> AllowedWriteExtensions { get; init; }

    /// <summary>Repository-relative paths that may be read but never written, e.g. a host's standing-instructions file.</summary>
    public IList<string> ProtectedPaths { get; } = [];

    /// <summary><c>read_file</c> refuses a file larger than this many bytes. Default 256 KiB.</summary>
    public int MaxReadBytes { get; set; } = 256 * 1024;

    /// <summary><c>list_files</c> lists at most this many entries per call. Default 500.</summary>
    public int MaxListEntries { get; set; } = 500;
}
