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

    /// <summary>
    /// How long <c>read_file</c>, <c>write_file</c> and <c>edit_file</c> wait for a file another holder is using,
    /// measured from the start of the call, before returning the distinct "the file is busy" result (ruling (j)). The
    /// wait covers two holders. First, this process's own concurrent calls on the same path: every call takes that
    /// path's in-process lock before it opens the file. Second, a holder outside the process: when the leaf file's
    /// open fails with a sharing violation or a lock violation on Windows, or <c>EBUSY</c> on Linux, the open is
    /// retried with a backoff that starts at 10 ms and doubles up to 250 ms, until this much time has passed since the
    /// call began (ruling (k)). Directory opens are not retried: they request read access and share read and write,
    /// so they do not contend with this tool's own opens. Default 5 seconds. <see cref="TimeSpan.Zero"/> makes one
    /// attempt with no wait; <see cref="Timeout.InfiniteTimeSpan"/> waits until the holder lets go or the call is
    /// cancelled. Any other negative value, or one over <see cref="int.MaxValue"/> milliseconds, is refused by
    /// <c>UseRunWorkspaceTools</c>.
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
