using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Logging;
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
/// against the file's <em>real</em> final name, never the raw input a model supplied. Reads are ungated.
/// </para>
/// <para>
/// <b>Every check that decides a write runs twice.</b> Containment, the protected-path check and the extension
/// allow-list all run once before any filesystem change, against the path <see cref="WorkspacePath.Resolve"/>
/// returned — so nothing is created, neither the file nor a parent directory, unless that pre-check passes — and
/// once more after the file is actually open, against the open handle's real path
/// (<see cref="WorkspacePath.FinalPathOfHandle"/>) relative to the workspace's canonicalised root
/// (<see cref="WorkspacePath.CanonicalizeRoot"/>). The second run exists because <see cref="WorkspacePath.Resolve"/>
/// only returns a string: a directory swapped for a link to somewhere else — including the workspace root itself —
/// between that call and the actual open would let the pre-check's string comparison pass a target the write must
/// not reach. A write is opened with <see cref="FileMode.Open"/>, falling back to <see cref="FileMode.CreateNew"/>
/// only when the file is absent; <see cref="FileMode.CreateNew"/> refuses if anything — even a dangling
/// symlink — already occupies that name, which is what stops the fallback from creating through a link swapped in
/// during the same gap. A file this call created and then refused on the second check is deleted by the handle's
/// real path, never by the pre-open string; any parent directories this call created are removed the same way,
/// innermost first, and only as long as each is empty. Where the handle's real path cannot be determined at all —
/// an unsupported platform — the call fails closed. This is not airtight against every race: a hard link to a
/// protected or disallowed file has no distinct name of its own to check against, and creating one is out of
/// scope for anything these tools expose, so that class of alias is a known, accepted limit rather than something
/// checked for here. <see cref="WorkspacePath.FinalPathOfHandle"/> is not supported on macOS (no
/// <c>/proc/self/fd</c>) and fails closed there; nothing in this class ships on macOS today.
/// </para>
/// <para>
/// <b>No grant check lives here.</b> The host binds <c>workspace__write_*</c> and <c>workspace__edit_*</c> to a
/// policy in <c>ToolPolicies</c>; this class only enforces confinement and the extension allow-list.
/// </para>
/// </remarks>
/// <param name="workspaces">Looks up the calling run's workspace.</param>
/// <param name="options">The host-wide write ceiling, protected paths and size limits.</param>
/// <param name="listeners">Notified with the changed path after a successful write or edit.</param>
/// <param name="logger">Logs a listener's exception; required, since a thrown listener exception must never surface any other way.</param>
[ThalosToolType]
public sealed partial class WorkspaceTools(IRunWorkspaceProvider workspaces, RunWorkspaceToolOptions options, IEnumerable<IRunWorkspaceChangeListener> listeners, ILogger<WorkspaceTools> logger)
{
    private const string NoWorkspace = "error: this turn has no run workspace";
    private const string GenericRefusal = "error: the path is not permitted.";

    /// <summary>
    /// Test-only seam: invoked with the path <see cref="OpenExisting"/> or <see cref="OpenForWrite"/> is about to
    /// open, immediately before it opens it, so a test can deterministically simulate the check-to-use race the
    /// type-level remarks describe — e.g. swapping a directory for a link between <see cref="WorkspacePath.Resolve"/>
    /// and the open — instead of depending on real timing. Always <see langword="null"/> in production; instance-level
    /// and internal, so only a test in this assembly's <c>InternalsVisibleTo</c> grant, holding its own instance, can
    /// set it — never shared, mutable state across instances or tests.
    /// </summary>
    internal Action<string>? BeforeOpenForTesting { get; set; }

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

        var opened = OpenExisting(path, target.Resolved!, FileAccess.Read, FileShare.ReadWrite);
        if (!opened.Ok)
        {
            return opened.Error!;
        }

        using var confined = opened.Value!;
        if (!IsContained(confined.RealPath, target.CanonicalRoot!))
        {
            return GenericRefusal;
        }

        var read = await ReadBoundedAsync(confined.Stream, path, ct).ConfigureAwait(false);
        return read.Error ?? read.Text!;
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

        if (WorkspacePath.CanonicalizeRoot(workspace.Root) is not { } canonicalRoot)
        {
            return GenericRefusal;
        }

        var start = ResolveListStart(workspace.Root, canonicalRoot, directory);
        if (start.Error is { } startError)
        {
            return startError;
        }

        if (!Directory.Exists(start.Path))
        {
            return $"error: '{directory ?? "."}' is not a directory.";
        }

        var entries = new List<string>();
        Walk(canonicalRoot, start.Path!, entries, options.MaxListEntries, ct);
        entries.Sort(StringComparer.Ordinal);

        return entries.Count == 0 ? "(empty)" : FormatListing(entries, options.MaxListEntries);
    }

    private static (string? Path, string? Error) ResolveListStart(string root, string canonicalRoot, string? directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return (canonicalRoot, null);
        }

        var resolved = WorkspacePath.Resolve(root, directory);
        return resolved.IsSuccess ? (resolved.Value, null) : (null, "error: " + resolved.Error.Message);
    }

    private static string FormatListing(List<string> entries, int limit)
    {
        var truncated = entries.Count > limit;
        var shown = truncated ? entries.GetRange(0, limit) : entries;

        var sb = new StringBuilder();
        foreach (var entry in shown)
        {
            sb.Append(entry).Append('\n');
        }

        if (truncated)
        {
            sb.Append("... (truncated)");
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
        var canonicalRoot = target.CanonicalRoot!;
        var resolved = target.Resolved!;

        // Pre-check: nothing is created before this passes.
        var pre = CheckWrite(caller, canonicalRoot, resolved, out var preExtension);
        if (pre != WriteGate.Ok)
        {
            return FormatGate(pre, path, preExtension);
        }

        var createdDirectories = CreateDirectoryChain(Path.GetDirectoryName(resolved));

        var opened = OpenForWrite(path, resolved);
        if (!opened.Ok)
        {
            RemoveCreatedDirectories(createdDirectories);
            return opened.Error!;
        }

        var confined = opened.Value!;

        // Post-check: the same three checks, now against the handle's real path.
        var post = CheckWrite(caller, canonicalRoot, confined.RealPath, out var postExtension);
        if (post != WriteGate.Ok)
        {
            CleanUpRefusedWrite(confined, createdDirectories);
            return FormatGate(post, path, postExtension);
        }

        int byteCount;
        string relativePath;
        try
        {
            byteCount = await WriteAllBytesAsync(confined.Stream, content, ct).ConfigureAwait(false);
            relativePath = RelativeToRoot(canonicalRoot, confined.RealPath);
        }
        finally
        {
            confined.Dispose();
        }

        Notify(workspace.RunId, relativePath);
        return $"wrote {byteCount} bytes to '{path}'.";
    }

    /// <summary>Disposes a write's handle and, since the post-check refused it, undoes exactly what this call itself created: the file, if <see cref="ConfinedHandle.CreatedNew"/>, and any parent directories, innermost first.</summary>
    private static void CleanUpRefusedWrite(ConfinedHandle confined, List<string> createdDirectories)
    {
        confined.Dispose();
        if (confined.CreatedNew)
        {
            TryDelete(confined.RealPath);
        }

        RemoveCreatedDirectories(createdDirectories);
    }

    private static async Task<int> WriteAllBytesAsync(FileStream stream, string content, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.SetLength(0);
        stream.Position = 0;
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        return bytes.Length;
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
        var canonicalRoot = target.CanonicalRoot!;
        var resolved = target.Resolved!;

        var pre = CheckWrite(caller, canonicalRoot, resolved, out var preExtension);
        if (pre != WriteGate.Ok)
        {
            return FormatGate(pre, path, preExtension);
        }

        var opened = OpenExisting(path, resolved, FileAccess.ReadWrite, FileShare.None);
        if (!opened.Ok)
        {
            return opened.Error!;
        }

        var confined = opened.Value!;
        string? relativePath;
        string result;
        try
        {
            (relativePath, result) = await ApplyEditAsync(caller, canonicalRoot, confined, path, oldText, newText, ct).ConfigureAwait(false);
        }
        finally
        {
            confined.Dispose();
        }

        if (relativePath is null)
        {
            return result;
        }

        Notify(workspace.RunId, relativePath);
        return result;
    }

    /// <summary>
    /// The post-check, bounded read, exactly-once replacement and write for <c>edit_file</c>. Returns the changed
    /// path relative to the canonical root and the success text on success, or a null path and the refusal or error
    /// text otherwise — the caller notifies listeners only when the path is non-null.
    /// </summary>
    private async Task<(string? RelativePath, string Result)> ApplyEditAsync(
        ISecurityContext caller, string canonicalRoot, ConfinedHandle confined, string path, string oldText, string newText, CancellationToken ct)
    {
        var post = CheckWrite(caller, canonicalRoot, confined.RealPath, out var postExtension);
        if (post != WriteGate.Ok)
        {
            return (null, FormatGate(post, path, postExtension));
        }

        var read = await ReadBoundedAsync(confined.Stream, path, ct).ConfigureAwait(false);
        if (read.Error is { } sizeError)
        {
            return (null, sizeError);
        }

        var occurrences = CountOccurrences(read.Text!, oldText);
        if (occurrences != 1)
        {
            return (null, $"error: oldText occurs {occurrences} times in '{path}'; it must occur exactly once.");
        }

        var updated = read.Text!.Replace(oldText, newText, StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(updated);

        confined.Stream.Position = 0;
        confined.Stream.SetLength(0);
        await confined.Stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await confined.Stream.FlushAsync(ct).ConfigureAwait(false);

        return (RelativeToRoot(canonicalRoot, confined.RealPath), $"edited '{path}'.");
    }

    /// <summary>Resolves the calling run's workspace, its canonicalised root, and confines <paramref name="path"/> to it.</summary>
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

        if (WorkspacePath.CanonicalizeRoot(workspace.Root) is not { } canonicalRoot)
        {
            return PathResolution.Failure(GenericRefusal);
        }

        var resolved = WorkspacePath.Resolve(workspace.Root, path);
        return resolved.IsSuccess
            ? PathResolution.Success(workspace, canonicalRoot, resolved.Value)
            : PathResolution.Failure("error: " + resolved.Error.Message);
    }

    /// <summary>The outcome of <see cref="CheckWrite"/>: which of the three checks, if any, refuses a write.</summary>
    private enum WriteGate
    {
        Ok,
        NotPermitted,
        Protected,
        Extension,
    }

    /// <summary>
    /// Containment, the protected-path check and the extension allow-list, run against <paramref name="candidate"/>
    /// relative to <paramref name="canonicalRoot"/> — called once before any filesystem change (on the resolved,
    /// pre-open path) and once more after the open (on the handle's real path), per the type-level remarks.
    /// </summary>
    private WriteGate CheckWrite(ISecurityContext caller, string canonicalRoot, string candidate, out string? extensionMessage)
    {
        extensionMessage = null;

        if (!IsContained(candidate, canonicalRoot))
        {
            return WriteGate.NotPermitted;
        }

        if (IsProtected(canonicalRoot, candidate))
        {
            return WriteGate.Protected;
        }

        if (RefuseExtension(caller, candidate) is { } refusal)
        {
            extensionMessage = refusal;
            return WriteGate.Extension;
        }

        return WriteGate.Ok;
    }

    private static string FormatGate(WriteGate gate, string path, string? extensionMessage) => gate switch
    {
        WriteGate.NotPermitted => GenericRefusal,
        WriteGate.Protected => Protected(path),
        WriteGate.Extension => extensionMessage!,
        _ => throw new InvalidOperationException($"{nameof(CheckWrite)} returned {nameof(WriteGate.Ok)}; there is nothing to format."),
    };

    /// <summary>Opens an existing file for <c>read_file</c> (read-only) or <c>edit_file</c> (read-write, exclusive). Never creates one.</summary>
    private ConfinedHandleResult OpenExisting(string originalPath, string resolvedPath, FileAccess access, FileShare share)
    {
        BeforeOpenForTesting?.Invoke(resolvedPath);

        FileStream stream;
        try
        {
            stream = new FileStream(resolvedPath, FileMode.Open, access, share);
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
        if (real is null)
        {
            stream.Dispose();
            return ConfinedHandleResult.Failure(GenericRefusal);
        }

        return ConfinedHandleResult.Success(new ConfinedHandle(stream, real, createdNew: false));
    }

    /// <summary>
    /// Opens <paramref name="resolvedPath"/> for <c>write_file</c>: <see cref="FileMode.Open"/> when it already
    /// exists, falling back to <see cref="FileMode.CreateNew"/> only when it is absent — see the type-level remarks
    /// for why the fallback specifically must not be <see cref="FileMode.OpenOrCreate"/>.
    /// </summary>
    private ConfinedHandleResult OpenForWrite(string originalPath, string resolvedPath)
    {
        BeforeOpenForTesting?.Invoke(resolvedPath);

        FileStream stream;
        var createdNew = false;
        try
        {
            stream = new FileStream(resolvedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            try
            {
                stream = new FileStream(resolvedPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                createdNew = true;
            }
            catch (Exception ex2) when (ex2 is IOException or UnauthorizedAccessException)
            {
                return ConfinedHandleResult.Failure(GenericRefusal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ConfinedHandleResult.Failure(GenericRefusal);
        }

        var real = WorkspacePath.FinalPathOfHandle(stream.SafeFileHandle);
        if (real is null)
        {
            stream.Dispose();
            if (createdNew)
            {
                TryDelete(resolvedPath);
            }

            return ConfinedHandleResult.Failure(GenericRefusal);
        }

        return ConfinedHandleResult.Success(new ConfinedHandle(stream, real, createdNew));
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

    /// <summary>Compares <paramref name="candidate"/>'s path relative to <paramref name="canonicalRoot"/> against <see cref="RunWorkspaceToolOptions.ProtectedPaths"/>, case-insensitively — never the raw input a model supplied.</summary>
    private bool IsProtected(string canonicalRoot, string candidate)
    {
        if (options.ProtectedPaths.Count == 0)
        {
            return false;
        }

        var relative = RelativeToRoot(canonicalRoot, candidate);
        foreach (var protectedPath in options.ProtectedPaths)
        {
            if (string.Equals(relative, NormalizeSeparators(protectedPath), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string RelativeToRoot(string canonicalRoot, string realPath) => NormalizeSeparators(Path.GetRelativePath(canonicalRoot, realPath));

    private static string NormalizeSeparators(string path) => path.Replace('\\', '/');

    private static string Protected(string path) => $"error: '{path}' is protected and cannot be written.";

    /// <summary>
    /// Notifies every listener with <paramref name="relativePath"/> — relative to the canonical root, forward-slash
    /// separated — after the write's own handle has already been disposed (a listener that reads the file back must
    /// not collide with a still-open, exclusively-shared handle) and only for a write that has already fully
    /// succeeded: a listener's own exception is contained and logged, never allowed to turn a completed write into a
    /// failure the caller sees.
    /// </summary>
    private void Notify(Guid runId, string relativePath)
    {
        foreach (var listener in listeners)
        {
            try
            {
                listener.OnFilesChanged(runId, [relativePath]);
            }
            catch (Exception ex)
            {
                LogListenerThrew(logger, ex.GetType().Name, ex);
            }
        }
    }

    [LoggerMessage(EventId = 5801, Level = LogLevel.Warning, Message = "A workspace change listener threw handling a file change ({ExceptionType}); the write it followed still succeeded.")]
    private static partial void LogListenerThrew(ILogger logger, string exceptionType, Exception exception);

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

    /// <summary>
    /// Reads at most <see cref="RunWorkspaceToolOptions.MaxReadBytes"/> plus one byte from <paramref name="stream"/>
    /// — never trusting <see cref="FileStream.Length"/>, which a special or growing file can misreport — and
    /// refuses if that one extra byte was actually read, rather than silently truncating.
    /// </summary>
    private async Task<(string? Text, string? Error)> ReadBoundedAsync(FileStream stream, string path, CancellationToken ct)
    {
        var limit = options.MaxReadBytes;
        var buffer = new byte[limit + 1];
        stream.Position = 0;

        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        if (total > limit)
        {
            return (null, $"error: '{path}' is over the {limit}-byte read limit.");
        }

        return (Encoding.UTF8.GetString(buffer, 0, total), null);
    }

    /// <summary>Walks <paramref name="directory"/> recursively, collecting paths relative to <paramref name="root"/>; skips <c>.git</c> and does not follow reparse points. Stops once one more than <paramref name="limit"/> entries have been collected, rather than walking the whole tree only to truncate the result afterwards.</summary>
    private static void Walk(string root, string directory, List<string> results, int limit, CancellationToken ct)
    {
        if (results.Count > limit)
        {
            return;
        }

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
            if (results.Count > limit)
            {
                return;
            }

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
                Walk(root, child, results, limit, ct);
            }
            else
            {
                results.Add(relative);
            }
        }
    }

    /// <summary>
    /// Creates every directory from the deepest existing ancestor of <paramref name="directory"/> down to
    /// <paramref name="directory"/> itself, and returns exactly the ones this call created — nothing that already
    /// existed — so a later refusal can remove exactly those and no others.
    /// </summary>
    private static List<string> CreateDirectoryChain(string? directory)
    {
        var created = new List<string>();
        if (string.IsNullOrEmpty(directory))
        {
            return created;
        }

        var toCreate = new Stack<string>();
        var current = directory;
        while (!Directory.Exists(current))
        {
            toCreate.Push(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            current = parent;
        }

        while (toCreate.Count > 0)
        {
            var dir = toCreate.Pop();
            Directory.CreateDirectory(dir);
            created.Add(dir);
        }

        return created;
    }

    /// <summary>Removes exactly the directories <see cref="CreateDirectoryChain"/> created, innermost first, stopping at the first one that is not both present and empty.</summary>
    private static void RemoveCreatedDirectories(List<string> created)
    {
        for (var i = created.Count - 1; i >= 0; i--)
        {
            try
            {
                Directory.Delete(created[i]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                break;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best-effort cleanup of a file this call itself created before refusing it
        }
    }

    private static bool IsContained(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>The outcome of <see cref="ResolveAsync"/>: either a workspace, its canonicalised root and a confined, resolved path, or ready-to-return error text.</summary>
    private readonly struct PathResolution
    {
        private PathResolution(RunWorkspace? workspace, string? canonicalRoot, string? resolved, string? error)
        {
            Workspace = workspace;
            CanonicalRoot = canonicalRoot;
            Resolved = resolved;
            Error = error;
        }

        public RunWorkspace? Workspace { get; }

        public string? CanonicalRoot { get; }

        public string? Resolved { get; }

        public string? Error { get; }

        public bool Ok => Error is null;

        public static PathResolution Success(RunWorkspace workspace, string canonicalRoot, string resolved) => new(workspace, canonicalRoot, resolved, null);

        public static PathResolution Failure(string error) => new(null, null, null, error);
    }

    /// <summary>The outcome of opening a file: either an open, re-checked handle, or ready-to-return error text.</summary>
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

    /// <summary>An open file handle together with its real, symlink-resolved path, and whether this call itself created the file (via <see cref="FileMode.CreateNew"/>) — see <see cref="OpenForWrite"/>.</summary>
    private sealed class ConfinedHandle(FileStream stream, string realPath, bool createdNew) : IDisposable
    {
        public FileStream Stream { get; } = stream;

        public string RealPath { get; } = realPath;

        public bool CreatedNew { get; } = createdNew;

        public void Dispose() => Stream.Dispose();
    }
}
