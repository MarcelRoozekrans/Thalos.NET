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

    /// <summary>
    /// When true, the host-wide ceiling allows every extension, and only a caller's
    /// <c>thalos.workspace.write_extensions</c> claim narrows it. Only for workspaces whose build files are never evaluated
    /// on the host, such as a run sandbox. A host that loads a run's solution in-process must keep an extension list.
    /// </summary>
    public bool AllowAnyWriteExtension { get; set; }

    /// <summary>Repository-relative paths that may be read but never written, e.g. a host's standing-instructions file. An entry ending in <c>/</c> protects that directory and everything under it.</summary>
    public IList<string> ProtectedPaths { get; } = [];

    /// <summary><c>read_file</c> refuses a file larger than this many bytes. Default 256 KiB.</summary>
    public int MaxReadBytes { get; set; } = 256 * 1024;

    /// <summary><c>list_files</c> lists at most this many entries per call. Default 500.</summary>
    public int MaxListEntries { get; set; } = 500;

    /// <summary>
    /// How long <c>read_file</c>, <c>write_file</c>, <c>edit_file</c> and <c>list_files</c> wait for a file or
    /// directory another holder is using, measured from the start of the call, before returning the distinct "the file
    /// is busy" result (ruling (j)). The wait covers these holders:
    /// <list type="bullet">
    /// <item>This process's own concurrent calls on the same file: every call except <c>list_files</c> takes that
    /// path's in-process lock before it opens the file.</item>
    /// <item>A holder of the file outside the process: when the file's open fails with a sharing violation or a lock
    /// violation on Windows, or <c>EBUSY</c> on Linux, the open is retried with a backoff that starts at 10 ms and
    /// doubles up to 250 ms (ruling (k)).</item>
    /// <item>A holder of a directory on the way to the file outside the process: when a directory's open fails with a
    /// sharing violation, or, for <c>write_file</c>, the directory is gone again between its creation and its open,
    /// every directory the call holds is released and the walk starts again from the workspace root, with the same
    /// backoff (round-5 ruling (t)).</item>
    /// <item>A holder outside the process of a subdirectory <c>list_files</c> walks, or a subdirectory that is gone
    /// by the time the walk opens it: the listing is rebuilt from its start with the same backoff, never returned
    /// without that subtree (ruling (e)).</item>
    /// </list>
    /// Directory opens never contend with this process's own calls: they request read access and share read and
    /// write; a call removes a directory it created only when no other call holds that directory, and leaves it in
    /// place otherwise; and a call about to open a directory that is being removed waits for the removal to finish.
    /// That wait lasts only as long as a few file-system calls and does not count against this timeout. Default 5
    /// seconds.
    /// <see cref="TimeSpan.Zero"/> makes one attempt with no wait; <see cref="Timeout.InfiniteTimeSpan"/> waits until
    /// the holder lets go or the call is cancelled. Any other negative value, or one over <see cref="int.MaxValue"/>
    /// milliseconds, is refused by <c>UseRunWorkspaceTools</c>.
    /// </summary>
    public TimeSpan ContentionTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Throws <see cref="ArgumentException"/> naming the offending member when a value cannot work:
    /// <see cref="ContentionTimeout"/> outside <see cref="Timeout.InfiniteTimeSpan"/> or zero to
    /// <see cref="int.MaxValue"/> milliseconds, <see cref="MaxReadBytes"/> negative or too large to buffer, or
    /// <see cref="MaxListEntries"/> negative. <paramref name="paramName"/> is the caller's parameter that produced
    /// these options.
    /// </summary>
    internal void Validate(string paramName)
    {
        if (ContentionTimeout != Timeout.InfiniteTimeSpan
            && (ContentionTimeout < TimeSpan.Zero || ContentionTimeout.TotalMilliseconds > int.MaxValue))
        {
            throw new ArgumentException(
                $"RunWorkspaceToolOptions.ContentionTimeout must be Timeout.InfiniteTimeSpan or between zero and {int.MaxValue} milliseconds; it was {ContentionTimeout}.",
                paramName);
        }

        if (MaxReadBytes < 0 || MaxReadBytes >= Array.MaxLength)
        {
            throw new ArgumentException(
                $"RunWorkspaceToolOptions.MaxReadBytes must be between zero and {Array.MaxLength - 1}; it was {MaxReadBytes}.",
                paramName);
        }

        if (MaxListEntries < 0)
        {
            throw new ArgumentException($"RunWorkspaceToolOptions.MaxListEntries must not be negative; it was {MaxListEntries}.", paramName);
        }
    }
}
