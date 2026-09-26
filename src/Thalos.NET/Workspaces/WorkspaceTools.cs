using System.ComponentModel;
using System.Text;
using ZeroAlloc.Authorization;

namespace Thalos.Workspaces;

/// <summary>
/// The <c>workspace</c> tool source's methods: <c>workspace__read_file</c>, <c>workspace__list_files</c>,
/// <c>workspace__write_file</c> and <c>workspace__edit_file</c>. Every call is confined to the calling run's
/// workspace — the <see cref="ISecurityContext"/> caller's <see cref="RunWorkspaceClaims.RunId"/> claim selects the
/// workspace via <paramref name="workspaces"/>, and every path an agent supplies is confined to that workspace's
/// root through <see cref="WorkspacePath.Resolve"/> before it ever touches disk. A caller with no run claim, or
/// whose run has no recorded workspace, is refused with a generic message that names neither reason.
/// </summary>
/// <remarks>
/// <para>
/// <b>Writes are allow-listed by extension (ruling R29).</b> <c>write_file</c> and <c>edit_file</c> refuse any
/// extension not on <see cref="RunWorkspaceToolOptions.AllowedWriteExtensions"/> — narrowed further by the caller's
/// own <see cref="RunWorkspaceClaims.WriteExtensions"/> grant when it carries one — checked case-insensitively
/// against the file's <em>resolved</em> final name, never the raw input a model supplied. Reads are ungated: this
/// class has no path that lets a write bypass the allow-list, because the allow-list check runs after
/// <see cref="WorkspacePath.Resolve"/> and before any content is written, on every write and edit call, with no
/// other route into <see cref="System.IO.FileStream"/>'s write mode.
/// </para>
/// <para>
/// <b>The gap between check and use.</b> <see cref="WorkspacePath.Resolve"/> returns a string; a symlink swapped in
/// after that call returns but before the file is actually opened would let a plain string comparison pass an
/// unintended target. <see cref="OpenConfined"/> closes that gap for the one operation it can be closed for: it
/// opens the file once, then re-derives the real path from the open handle itself
/// (<see cref="WorkspacePath.FinalPathOfHandle"/>) and checks <em>that</em> against the workspace root and, for a
/// write, against the allow-list — never re-opening by path, and never trusting the pre-open string for anything
/// but locating what to open. Where that re-check cannot run (an unsupported platform), the call fails closed.
/// </para>
/// <para>
/// <b>No grant check lives here.</b> The host binds <c>workspace__write_*</c> and <c>workspace__edit_*</c> to a
/// policy in <c>ToolPolicies</c>; this class only enforces confinement and the extension allow-list.
/// </para>
/// </remarks>
/// <param name="workspaces">Looks up the calling run's workspace.</param>
/// <param name="options">The host-wide write ceiling, protected paths and size limits.</param>
/// <param name="listeners">Notified with the changed path after a successful write or edit.</param>
[ThalosToolType]
public sealed class WorkspaceTools(IRunWorkspaceProvider workspaces, RunWorkspaceToolOptions options, IEnumerable<IRunWorkspaceChangeListener> listeners)
{
    private const string NoWorkspace = "error: this turn has no run workspace";

    /// <summary>
    /// Test-only seam: invoked with the path <see cref="OpenConfined"/> is about to open, immediately before it
    /// opens it, so a test can deterministically simulate the check-to-use race the type-level remarks describe —
    /// e.g. swapping a directory for a link between <see cref="WorkspacePath.Resolve"/> and the open — instead of
    /// depending on real timing. Always <see langword="null"/> in production; internal so only tests in this
    /// assembly's <c>InternalsVisibleTo</c> grant can set it.
    /// </summary>
    internal static Action<string>? BeforeOpenForTesting { get; set; }

    /// <summary><c>workspace__read_file</c>: reads a text file from the run's workspace. Ungated by the write allow-list.</summary>
    [ThalosTool("read_file")]
    [Description("Read a text file from the run's workspace. The path is relative to the workspace root.")]
    public async Task<string> ReadFile(ISecurityContext caller, [Description("Path relative to the workspace root.")] string path, CancellationToken ct = default)
    {
        var target = await ResolveAsync(caller, path, ct).ConfigureAwait(false);
        if (!target.Ok)
        {
            return target.Error!;
        }

        var opened = OpenConfined(target.Workspace!, path, target.Resolved!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (!opened.Ok)
        {
            return opened.Error!;
        }

        using var confined = opened.Value!;
        if (confined.Stream.Length > options.MaxReadBytes)
        {
            return $"error: '{path}' is {confined.Stream.Length} bytes, over the {options.MaxReadBytes}-byte read limit.";
        }

        using var reader = new StreamReader(confined.Stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    /// <summary><c>workspace__list_files</c>: lists files and directories under the workspace root (or a subdirectory), skipping <c>.git</c> and not following links.</summary>
    [ThalosTool("list_files")]
    [Description("List files and directories in the run's workspace, recursively. Directories are shown with a trailing '/'.")]
    public async Task<string> ListFiles(ISecurityContext caller, [Description("Directory relative to the workspace root; omit for the whole workspace.")] string? directory = null, CancellationToken ct = default)
    {
        if (RunWorkspaceClaims.RunIdOf(caller) is not { } runId)
        {
            return NoWorkspace;
        }

        var workspace = await workspaces.FindAsync(runId, ct).ConfigureAwait(false);
        if (workspace is null)
        {
            return NoWorkspace;
        }

        string start;
        if (string.IsNullOrEmpty(directory))
        {
            start = workspace.Root;
        }
        else
        {
            var resolved = WorkspacePath.Resolve(workspace.Root, directory);
            if (resolved.IsFailure)
            {
                return "error: " + resolved.Error.Message;
            }

            start = resolved.Value;
        }

        if (!Directory.Exists(start))
        {
            return $"error: '{directory ?? "."}' is not a directory.";
        }

        var entries = new List<string>();
        Walk(workspace.Root, start, entries, ct);
        entries.Sort(StringComparer.Ordinal);

        if (entries.Count == 0)
        {
            return "(empty)";
        }

        var truncated = entries.Count > options.MaxListEntries;
        var shown = truncated ? entries.GetRange(0, options.MaxListEntries) : entries;

        var sb = new StringBuilder();
        foreach (var entry in shown)
        {
            sb.Append(entry).Append('\n');
        }

        if (truncated)
        {
            sb.Append("... (").Append(entries.Count - options.MaxListEntries).Append(" more)");
        }

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary><c>workspace__write_file</c>: creates or overwrites a text file. Refused for a protected path or a disallowed extension (ruling R29).</summary>
    [ThalosTool("write_file")]
    [Description("Create or overwrite a text file in the run's workspace, creating parent directories as needed. Refused for a protected path or a file extension this run is not allowed to write.")]
    public async Task<string> WriteFile(ISecurityContext caller, [Description("Path relative to the workspace root.")] string path, [Description("The file's new full content.")] string content, CancellationToken ct = default)
    {
        var target = await ResolveAsync(caller, path, ct).ConfigureAwait(false);
        if (!target.Ok)
        {
            return target.Error!;
        }

        var workspace = target.Workspace!;
        var resolved = target.Resolved!;

        if (IsProtected(workspace, resolved))
        {
            return Protected(path);
        }

        var directory = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var existedBefore = File.Exists(resolved);

        var opened = OpenConfined(workspace, path, resolved, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!opened.Ok)
        {
            return opened.Error!;
        }

        var confined = opened.Value!;
        try
        {
            if (RefuseExtension(caller, confined.RealPath) is { } refusal)
            {
                confined.Dispose();
                if (!existedBefore)
                {
                    TryDeleteFreshFile(resolved);
                }

                return refusal;
            }

            var bytes = Encoding.UTF8.GetBytes(content);
            confined.Stream.SetLength(0);
            confined.Stream.Position = 0;
            await confined.Stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await confined.Stream.FlushAsync(ct).ConfigureAwait(false);

            Notify(workspace.RunId, path);
            return $"wrote {bytes.Length} bytes to '{path}'.";
        }
        finally
        {
            confined.Dispose();
        }
    }

    /// <summary><c>workspace__edit_file</c>: replaces a single, exact occurrence of <paramref name="oldText"/>. Refused unless it occurs exactly once, for a protected path, or a disallowed extension (ruling R29).</summary>
    [ThalosTool("edit_file")]
    [Description("Replace one exact occurrence of oldText with newText in a file already in the run's workspace. Refused unless oldText occurs exactly once, for a protected path, or a file extension this run is not allowed to write.")]
    public async Task<string> EditFile(
        ISecurityContext caller,
        [Description("Path relative to the workspace root.")] string path,
        [Description("The exact text to replace. Must occur exactly once in the file.")] string oldText,
        [Description("The replacement text.")] string newText,
        CancellationToken ct = default)
    {
        var target = await ResolveAsync(caller, path, ct).ConfigureAwait(false);
        if (!target.Ok)
        {
            return target.Error!;
        }

        var workspace = target.Workspace!;
        var resolved = target.Resolved!;

        if (IsProtected(workspace, resolved))
        {
            return Protected(path);
        }

        var opened = OpenConfined(workspace, path, resolved, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (!opened.Ok)
        {
            return opened.Error!;
        }

        var confined = opened.Value!;
        try
        {
            if (RefuseExtension(caller, confined.RealPath) is { } refusal)
            {
                return refusal;
            }

            string text;
            using (var reader = new StreamReader(confined.Stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
            {
                text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }

            var occurrences = CountOccurrences(text, oldText);
            if (occurrences != 1)
            {
                return $"error: oldText occurs {occurrences} times in '{path}'; it must occur exactly once.";
            }

            var updated = text.Replace(oldText, newText, StringComparison.Ordinal);
            var bytes = Encoding.UTF8.GetBytes(updated);

            confined.Stream.Position = 0;
            confined.Stream.SetLength(0);
            await confined.Stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await confined.Stream.FlushAsync(ct).ConfigureAwait(false);

            Notify(workspace.RunId, path);
            return $"edited '{path}'.";
        }
        finally
        {
            confined.Dispose();
        }
    }

    /// <summary>Resolves the calling run's workspace and confines <paramref name="path"/> to it. See the type-level remarks for what this does and does not close.</summary>
    private async Task<PathResolution> ResolveAsync(ISecurityContext caller, string path, CancellationToken ct)
    {
        if (RunWorkspaceClaims.RunIdOf(caller) is not { } runId)
        {
            return PathResolution.Failure(NoWorkspace);
        }

        var workspace = await workspaces.FindAsync(runId, ct).ConfigureAwait(false);
        if (workspace is null)
        {
            return PathResolution.Failure(NoWorkspace);
        }

        var resolved = WorkspacePath.Resolve(workspace.Root, path);
        return resolved.IsSuccess
            ? PathResolution.Success(workspace, resolved.Value)
            : PathResolution.Failure("error: " + resolved.Error.Message);
    }

    /// <summary>
    /// Opens <paramref name="resolvedPath"/> once and re-derives its real path from the open handle itself, so the
    /// caller never has to trust the pre-open string for anything but locating what to open — see the type-level
    /// remarks. Fails closed (the same generic message <see cref="WorkspacePath.Resolve"/> uses) when the real path
    /// cannot be determined, or does not land inside <paramref name="workspace"/>'s root.
    /// </summary>
    private static ConfinedHandleResult OpenConfined(RunWorkspace workspace, string originalPath, string resolvedPath, FileMode mode, FileAccess access, FileShare share)
    {
        BeforeOpenForTesting?.Invoke(resolvedPath);

        FileStream stream;
        try
        {
            stream = new FileStream(resolvedPath, mode, access, share);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ConfinedHandleResult.Failure($"error: '{originalPath}' does not exist.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ConfinedHandleResult.Failure(GenericRefusal);
        }

        var real = WorkspacePath.FinalPathOfHandle(stream.SafeFileHandle);
        if (real is null || !IsContained(real, workspace.Root))
        {
            stream.Dispose();
            return ConfinedHandleResult.Failure(GenericRefusal);
        }

        return ConfinedHandleResult.Success(new ConfinedHandle(stream, real));
    }

    /// <summary><see langword="null"/> when <paramref name="caller"/> may write a file whose real final path is <paramref name="realPath"/>; otherwise the error text.</summary>
    private string? RefuseExtension(ISecurityContext caller, string realPath)
    {
        var extension = Path.GetExtension(realPath);
        var allowed = AllowedExtensionsFor(caller);
        return allowed.Contains(extension) ? null : $"error: extension '{extension}' is not writable in this run; allowed: {FormatAllowed(allowed)}";
    }

    /// <summary>The host-wide ceiling, intersected with <paramref name="caller"/>'s own <see cref="RunWorkspaceClaims.WriteExtensions"/> grant when it carries one — always compared case-insensitively, regardless of the comparer the host built <see cref="RunWorkspaceToolOptions.AllowedWriteExtensions"/> with.</summary>
    private HashSet<string> AllowedExtensionsFor(ISecurityContext caller)
    {
        var ceiling = new HashSet<string>(options.AllowedWriteExtensions, StringComparer.OrdinalIgnoreCase);
        if (RunWorkspaceClaims.WriteExtensionsOf(caller) is { } grant)
        {
            ceiling.IntersectWith(grant);
        }

        return ceiling;
    }

    private static string FormatAllowed(HashSet<string> allowed) =>
        allowed.Count == 0 ? "(none)" : string.Join(", ", allowed.Order(StringComparer.OrdinalIgnoreCase));

    /// <summary>Compares <paramref name="resolvedPath"/>'s path relative to the workspace root against <see cref="RunWorkspaceToolOptions.ProtectedPaths"/>, case-insensitively — never the raw input a model supplied.</summary>
    private bool IsProtected(RunWorkspace workspace, string resolvedPath)
    {
        if (options.ProtectedPaths.Count == 0)
        {
            return false;
        }

        var relative = NormalizeSeparators(Path.GetRelativePath(workspace.Root, resolvedPath));
        foreach (var protectedPath in options.ProtectedPaths)
        {
            if (string.Equals(relative, NormalizeSeparators(protectedPath), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeSeparators(string path) => path.Replace('\\', '/');

    private static string Protected(string path) => $"error: '{path}' is protected and cannot be written.";

    private void Notify(Guid runId, string path)
    {
        foreach (var listener in listeners)
        {
            listener.OnFilesChanged(runId, [path]);
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        if (value.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    /// <summary>Walks <paramref name="directory"/> recursively, collecting paths relative to <paramref name="root"/>; skips <c>.git</c> and does not follow reparse points.</summary>
    private static void Walk(string root, string directory, List<string> results, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateFileSystemEntries(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (name.Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(child);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            var relative = NormalizeSeparators(Path.GetRelativePath(root, child));
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                results.Add(relative + "/");
                Walk(root, child, results, ct);
            }
            else
            {
                results.Add(relative);
            }
        }
    }

    private static void TryDeleteFreshFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // best-effort cleanup of a file this call just created before refusing it on extension grounds
        }
    }

    private static bool IsContained(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private const string GenericRefusal = "error: the path is not permitted.";

    /// <summary>The outcome of <see cref="ResolveAsync"/>: either a workspace and a confined, resolved path, or ready-to-return error text.</summary>
    private readonly struct PathResolution
    {
        private PathResolution(RunWorkspace? workspace, string? resolved, string? error)
        {
            Workspace = workspace;
            Resolved = resolved;
            Error = error;
        }

        public RunWorkspace? Workspace { get; }

        public string? Resolved { get; }

        public string? Error { get; }

        public bool Ok => Error is null;

        public static PathResolution Success(RunWorkspace workspace, string resolved) => new(workspace, resolved, null);

        public static PathResolution Failure(string error) => new(null, null, error);
    }

    /// <summary>The outcome of <see cref="OpenConfined"/>: either an open, re-checked handle, or ready-to-return error text.</summary>
    private readonly struct ConfinedHandleResult
    {
        private ConfinedHandleResult(ConfinedHandle? value, string? error)
        {
            Value = value;
            Error = error;
        }

        public ConfinedHandle? Value { get; }

        public string? Error { get; }

        public bool Ok => Error is null;

        public static ConfinedHandleResult Success(ConfinedHandle value) => new(value, null);

        public static ConfinedHandleResult Failure(string error) => new(null, error);
    }

    /// <summary>An open file handle together with its real, symlink-resolved path — see <see cref="OpenConfined"/>.</summary>
    private sealed class ConfinedHandle(FileStream stream, string realPath) : IDisposable
    {
        public FileStream Stream { get; } = stream;

        public string RealPath { get; } = realPath;

        public void Dispose() => Stream.Dispose();
    }
}
