using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

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

    /// <summary>The fixed, generic refusal — never a path, an errno, or a Win32 error code — shared with <see cref="PinnedDirectory"/> and <see cref="PinnedFile"/>, which fail closed with the same text whenever pinning or a per-level verification cannot succeed.</summary>
    internal const string GenericRefusalText = "error: the path is not permitted.";

    /// <summary>
    /// Returned instead of <see cref="GenericRefusalText"/> when a leaf open failed only because something else —
    /// this run's own concurrent call to the same path, or an outside process — currently holds it, and
    /// <see cref="RunWorkspaceToolOptions.ContentionTimeout"/> ran out waiting for it to let go. Ruling (j): this
    /// reveals nothing a caller could not already see, unlike a policy refusal, so it gets its own distinct text
    /// rather than folding into the one generic message.
    /// </summary>
    private const string BusyText = "error: the file is busy; try again.";

    /// <summary>The first backoff between leaf-open attempts that failed on contention from outside the process; each later one doubles, up to <see cref="MaxContentionRetryDelay"/>.</summary>
    private static readonly TimeSpan FirstContentionRetryDelay = TimeSpan.FromMilliseconds(10);

    /// <summary>The longest single backoff between leaf-open attempts.</summary>
    private static readonly TimeSpan MaxContentionRetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The per-path locks <c>read_file</c>, <c>write_file</c> and <c>edit_file</c> take before opening a leaf file,
    /// so this process's own calls on one file take turns instead of colliding on the open's share mode (round-3
    /// finding B1, round-4 finding N1). Keyed by the pre-open resolved path, since the point is to serialize before
    /// either call has opened anything. <see cref="LeafLockTable.Shared"/> in production, because the tool source
    /// creates a new instance per call; internal and settable only so a test can give an instance its own table and
    /// count its entries without seeing other tests' calls.
    /// </summary>
    internal LeafLockTable LeafLocks { get; set; } = LeafLockTable.Shared;

    /// <summary>
    /// Test-only seam: invoked with the candidate path or name a pinned open is about to try, immediately before it
    /// tries it, so a test can deterministically simulate the check-to-use race the
    /// type-level remarks describe — e.g. swapping a directory for a link between <see cref="WorkspacePath.Resolve"/>
    /// and the open — instead of depending on real timing. Always <see langword="null"/> in production; instance-level
    /// and internal, so only a test in this assembly's <c>InternalsVisibleTo</c> grant, holding its own instance, can
    /// set it — never shared, mutable state across instances or tests.
    /// </summary>
    internal Action<string>? BeforeOpenForTesting { get; set; }

    /// <summary>
    /// Test-only seam: invoked once when <c>write_file</c> cleans up a refusal, immediately before it removes any
    /// directory it created. When the refusal came after the leaf file was opened, the seam runs after that file is
    /// removed, if this call created it, and after its handle is closed: the window in which a cleanup by path string
    /// would re-resolve a name a swap could have redirected, and in which only the directory pins still hold the
    /// chain. Instance-level and internal, matching <see cref="BeforeOpenForTesting"/>.
    /// </summary>
    internal Action? BeforeCleanupForTesting { get; set; }

    /// <summary>
    /// Test-only seam: invoked once in <c>write_file</c> and <c>edit_file</c> after the post-open check passed and
    /// immediately before the leaf's path is verified a second time and the content is committed, so a test can move
    /// the file between the two verifications (ruling (d)). Instance-level and internal, matching
    /// <see cref="BeforeOpenForTesting"/>.
    /// </summary>
    internal Action? BeforeCommitForTesting { get; set; }

    /// <summary>
    /// Test-only seam: the architecture the Linux <c>O_DIRECTORY</c> and <c>O_NOFOLLOW</c> table is read for, in
    /// place of this process's own, so a test can pin the unknown-architecture refusal (ruling (a)) without running
    /// under one. Passed into every <see cref="PinnedDirectory.OpenRoot"/> call, the same way
    /// <see cref="BeforeOpenForTesting"/> is passed into every pinned open. Always <see langword="null"/> in
    /// production; instance-level and internal, so no test's setting reaches another instance.
    /// </summary>
    internal Architecture? ArchitectureOverrideForTesting { get; set; }

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

        var canonicalRoot = target.CanonicalRoot!;
        var resolved = target.Resolved!;
        var deadline = new ContentionDeadline(options.ContentionTimeout);
        var chainResult = PinExistingDirectoryChain(canonicalRoot, Path.GetDirectoryName(resolved) ?? canonicalRoot, path);
        if (!chainResult.Ok)
        {
            return chainResult.Error!;
        }

        var chain = chainResult.Chain!;
        try
        {
            // Ruling (k): a read takes the same per-path lock a write does, so it never meets this process's own
            // exclusive write handle on the leaf open.
            using var lease = await LeafLocks.AcquireAsync(resolved, deadline.Remaining, ct).ConfigureAwait(false);
            if (lease is null)
            {
                return BusyText;
            }

            var leafName = Path.GetFileName(resolved);
            var opened = await OpenLeafAsync(() => chain[^1].OpenExistingFile(leafName, readOnly: true, BeforeOpenForTesting), deadline, ct).ConfigureAwait(false);
            if (opened.IsFailure)
            {
                return FormatLeafOpenFailure(opened.Error, path);
            }

            using var file = opened.Value;
            if (!IsContained(file.RealPath, canonicalRoot))
            {
                return GenericRefusalText;
            }

            var read = await ReadBoundedAsync(file.Stream, path, ct).ConfigureAwait(false);
            return read.Error ?? read.Text!;
        }
        finally
        {
            DisposeChain(chain);
        }
    }

    /// <summary>
    /// Runs <paramref name="open"/>, and while it fails with <see cref="PinnedOpenOutcome.Contended"/> — a sharing or
    /// lock violation from a holder this process's own leaf locks do not cover, such as another process — waits and
    /// runs it again, with a backoff from <see cref="FirstContentionRetryDelay"/> doubling up to
    /// <see cref="MaxContentionRetryDelay"/>, until <paramref name="deadline"/> has no time left (ruling (k)). Returns
    /// the first result that is not contention, or the last contended one once the time is spent.
    /// </summary>
    private static async Task<Result<PinnedFile, PinnedOpenOutcome>> OpenLeafAsync(Func<Result<PinnedFile, PinnedOpenOutcome>> open, ContentionDeadline deadline, CancellationToken ct)
    {
        var delay = FirstContentionRetryDelay;
        while (true)
        {
            var opened = open();
            if (opened.IsSuccess || opened.Error != PinnedOpenOutcome.Contended)
            {
                return opened;
            }

            var remaining = deadline.Remaining;
            if (remaining == TimeSpan.Zero)
            {
                return opened;
            }

            await Task.Delay(remaining == Timeout.InfiniteTimeSpan || delay < remaining ? delay : remaining, ct).ConfigureAwait(false);
            delay = delay * 2 < MaxContentionRetryDelay ? delay * 2 : MaxContentionRetryDelay;
        }
    }

    /// <summary>The time left of <see cref="RunWorkspaceToolOptions.ContentionTimeout"/>, counted from when the call began: shared by the wait for the per-path lock and the retries of the leaf open, so together they never exceed it.</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly struct ContentionDeadline(TimeSpan timeout)
    {
        private readonly long _start = Stopwatch.GetTimestamp();

        /// <summary><see cref="Timeout.InfiniteTimeSpan"/> for an infinite timeout; otherwise what is left, never negative.</summary>
        public TimeSpan Remaining
        {
            get
            {
                if (timeout == Timeout.InfiniteTimeSpan)
                {
                    return Timeout.InfiniteTimeSpan;
                }

                var left = timeout - Stopwatch.GetElapsedTime(_start);
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }
    }

    /// <summary>Maps a failed leaf or ancestor open to the text a caller returns: "does not exist" for a missing target, the distinct busy text for contention, and the one generic refusal text for anything else (ruling (j)).</summary>
    private static string FormatLeafOpenFailure(PinnedOpenOutcome outcome, string path) => outcome switch
    {
        PinnedOpenOutcome.Missing => $"error: '{path}' does not exist.",
        PinnedOpenOutcome.Contended => BusyText,
        _ => GenericRefusalText,
    };

    /// <summary>
    /// Pins the directory chain down to <paramref name="targetDirectory"/> without creating anything missing — used
    /// by <c>read_file</c> and <c>edit_file</c> (ruling (h)), which must not bring a missing parent directory into
    /// existence as a side effect of a read. A missing or refused ancestor is reported the same way a missing leaf
    /// file is, via <see cref="FormatLeafOpenFailure"/>.
    /// </summary>
    private ChainResult PinExistingDirectoryChain(string canonicalRoot, string targetDirectory, string originalPath)
    {
        var rootPin = PinnedDirectory.OpenRoot(canonicalRoot, ArchitectureOverrideForTesting);
        if (rootPin.IsFailure)
        {
            return ChainResult.Failure(rootPin.Error);
        }

        var chain = new List<PinnedDirectory> { rootPin.Value };
        var relative = Path.GetRelativePath(canonicalRoot, targetDirectory);
        if (string.Equals(relative, ".", StringComparison.Ordinal))
        {
            return ChainResult.Success(chain);
        }

        try
        {
            var current = rootPin.Value;
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
            {
                var child = current.OpenChild(segment, BeforeOpenForTesting);
                if (child.IsFailure)
                {
                    DisposeChain(chain);
                    return ChainResult.Failure(FormatLeafOpenFailure(child.Error, originalPath));
                }

                chain.Add(child.Value);
                current = child.Value;
            }

            return ChainResult.Success(chain);
        }
        catch
        {
            DisposeChain(chain);
            throw;
        }
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
            return GenericRefusalText;
        }

        var start = ResolveListStart(workspace.Root, canonicalRoot, directory);
        if (start.Error is { } startError)
        {
            return startError;
        }

        using var startDirectory = start.Directory!;
        var entries = new List<string>();
        if (!Walk(startDirectory, "", entries, options.MaxListEntries, ct))
        {
            return GenericRefusalText;
        }

        entries.Sort(StringComparer.Ordinal);

        return entries.Count == 0 ? "(empty)" : FormatListing(entries, options.MaxListEntries);
    }

    /// <summary>
    /// Pins the directory <c>list_files</c> should start walking from — the canonical root itself, or a
    /// caller-supplied subdirectory, resolved and lexically validated via <see cref="WorkspacePath.Resolve"/> first,
    /// then reached by opening one already-verified level at a time, exactly as a write's directory chain does — so
    /// the starting point is pinned the same way, never re-resolved by a path string once found.
    /// </summary>
    private (PinnedDirectory? Directory, string? Error) ResolveListStart(string root, string canonicalRoot, string? directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return PinRoot(canonicalRoot);
        }

        var resolved = WorkspacePath.Resolve(root, directory);
        if (resolved.IsFailure)
        {
            return (null, "error: " + resolved.Error.Message);
        }

        var relative = Path.GetRelativePath(canonicalRoot, resolved.Value);
        if (string.Equals(relative, ".", StringComparison.Ordinal))
        {
            return PinRoot(canonicalRoot);
        }

        var rootPin = PinnedDirectory.OpenRoot(canonicalRoot, ArchitectureOverrideForTesting);
        if (rootPin.IsFailure)
        {
            return (null, GenericRefusalText);
        }

        // OpenChild's classified PinnedOpenOutcome is not distinguished here: list_files reported the same
        // "is not a directory" text for a missing or a mismatched target before ruling (h) introduced the
        // Missing/Contended split for read_file and edit_file, and nothing asks list_files to change that.
        var current = rootPin.Value;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            var child = current.OpenChild(segment);
            current.Dispose();
            if (child.IsFailure)
            {
                return (null, $"error: '{directory}' is not a directory.");
            }

            current = child.Value;
        }

        return (current, null);
    }

    private (PinnedDirectory? Directory, string? Error) PinRoot(string canonicalRoot)
    {
        var rootPin = PinnedDirectory.OpenRoot(canonicalRoot, ArchitectureOverrideForTesting);
        return rootPin.IsSuccess ? (rootPin.Value, null) : (null, GenericRefusalText);
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
        var deadline = new ContentionDeadline(options.ContentionTimeout);

        // Pre-check: nothing is created before this passes.
        var pre = CheckWrite(caller, canonicalRoot, resolved, out var preExtension);
        if (pre != WriteGate.Ok)
        {
            return FormatGate(pre, path, preExtension);
        }

        var chainResult = PinDirectoryChain(canonicalRoot, Path.GetDirectoryName(resolved) ?? canonicalRoot);
        if (!chainResult.Ok)
        {
            BeforeCleanupForTesting?.Invoke();
            return chainResult.Error!;
        }

        var chain = chainResult.Chain!;
        (string? RelativePath, string Result) written;
        try
        {
            // Serializes every call on this exact leaf path, so the leaf open below never contends with another
            // of this process's own calls: round-3 finding B1, round-4 finding N1. A bounded wait: exhausting it
            // returns the distinct busy text (ruling (j)) rather than hanging or refusing outright.
            using var lease = await LeafLocks.AcquireAsync(resolved, deadline.Remaining, ct).ConfigureAwait(false);
            if (lease is null)
            {
                return BusyText;
            }

            written = await OpenAndWriteLeafAsync(caller, canonicalRoot, path, resolved, content, chain, deadline, ct).ConfigureAwait(false);
        }
        finally
        {
            DisposeChain(chain);
        }

        // Notify only once the file's handle is closed and its per-path lock released: a listener that reads the
        // file back, even through read_file, must not meet either one.
        if (written.RelativePath is { } relativePath)
        {
            Notify(workspace.RunId, relativePath);
        }

        return written.Result;
    }

    /// <summary>The leaf open, post-check and write for <c>write_file</c>, once its directory chain is pinned and its per-path lock is held. Returns the changed path relative to the canonical root and the success text, or a null path and the refusal text. Split out from <see cref="WriteFile"/> only to keep it under the method-length limit.</summary>
    private async Task<(string? RelativePath, string Result)> OpenAndWriteLeafAsync(ISecurityContext caller, string canonicalRoot, string path, string resolved, string content, List<PinnedDirectory> chain, ContentionDeadline deadline, CancellationToken ct)
    {
        var leafName = Path.GetFileName(resolved);
        var opened = await OpenLeafAsync(() => chain[^1].OpenOrCreateFile(leafName, BeforeOpenForTesting), deadline, ct).ConfigureAwait(false);
        if (opened.IsFailure)
        {
            BeforeCleanupForTesting?.Invoke();
            RemoveCreatedDirectories(chain);
            return (null, opened.Error == PinnedOpenOutcome.Contended ? BusyText : GenericRefusalText);
        }

        var file = opened.Value;
        try
        {
            var post = CheckWrite(caller, canonicalRoot, file.RealPath, out var postExtension);
            if (post != WriteGate.Ok)
            {
                CleanUpRefusedFile(file, chain);
                return (null, FormatGate(post, path, postExtension));
            }

            // Ruling (d): verified once at open time (file.RealPath); verified again, live, right before the
            // content is actually committed — closing the gap between that first verification and this moment.
            BeforeCommitForTesting?.Invoke();
            if (!file.StillAtVerifiedPath())
            {
                CleanUpRefusedFile(file, chain);
                return (null, GenericRefusalText);
            }

            var byteCount = await WriteAllBytesAsync(file.Stream, content, ct).ConfigureAwait(false);
            return (RelativeToRoot(canonicalRoot, file.RealPath), $"wrote {byteCount} bytes to '{path}'.");
        }
        finally
        {
            file.Dispose();
        }
    }

    /// <summary>
    /// Since a check after the open refused the write: removes the file, if this call created it, through its own
    /// handle or its pinned parent's descriptor; closes the file's handle; then removes any directories this call
    /// created, innermost first. Never by a path string. <see cref="BeforeCleanupForTesting"/> runs between closing
    /// the file and removing the directories, so a test's swap there meets only the directory pins.
    /// </summary>
    private void CleanUpRefusedFile(PinnedFile file, List<PinnedDirectory> chain)
    {
        if (file.CreatedNew)
        {
            file.TryRemove();
        }

        // Closed now, not in the caller's finally: on Windows a directory this call also created is then empty
        // for RemoveCreatedDirectories, and only the directory pins still hold the chain.
        file.Dispose();
        BeforeCleanupForTesting?.Invoke();
        RemoveCreatedDirectories(chain);
    }

    /// <summary>
    /// Pins the whole chain of directories from the canonical root down to <paramref name="targetDirectory"/>,
    /// creating any level that is missing — never a path-based <see cref="Directory.CreateDirectory(string)"/> —
    /// and holds every level open, root first, so the type-level remarks' "hold the whole chain" applies from here
    /// until the caller disposes it via <see cref="DisposeChain"/>.
    /// </summary>
    private ChainResult PinDirectoryChain(string canonicalRoot, string targetDirectory)
    {
        var rootPin = PinnedDirectory.OpenRoot(canonicalRoot, ArchitectureOverrideForTesting);
        if (rootPin.IsFailure)
        {
            return ChainResult.Failure(rootPin.Error);
        }

        var chain = new List<PinnedDirectory> { rootPin.Value };
        var relative = Path.GetRelativePath(canonicalRoot, targetDirectory);
        if (string.Equals(relative, ".", StringComparison.Ordinal))
        {
            return ChainResult.Success(chain);
        }

        try
        {
            var current = rootPin.Value;
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
            {
                var child = current.CreateChild(segment, BeforeOpenForTesting);
                if (child.IsFailure)
                {
                    // Ruling (i): a chain-build failure removes every level this call created, not only the one
                    // that failed to verify — while every level is still pinned, before any handle is released.
                    RemoveCreatedDirectories(chain);
                    DisposeChain(chain);
                    return ChainResult.Failure(child.Error);
                }

                chain.Add(child.Value);
                current = child.Value;
            }

            return ChainResult.Success(chain);
        }
        catch
        {
            // Never leak an already-pinned ancestor if creating or verifying a deeper level throws instead of
            // returning a Result failure. Ruling (i) applies here too: a chain-build failure removes the levels
            // it created whether it surfaces as a Result or, as here, an exception.
            RemoveCreatedDirectories(chain);
            DisposeChain(chain);
            throw;
        }
    }

    /// <summary>Removes exactly the directories this call created, innermost first, stopping at the first one that was not created by this call or that removal itself refuses — e.g. because it is not empty. Never the root.</summary>
    private static void RemoveCreatedDirectories(List<PinnedDirectory> chain)
    {
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (!chain[i].WasCreated || !chain[i].TryRemoveSelf())
            {
                break;
            }
        }
    }

    private static void DisposeChain(List<PinnedDirectory> chain)
    {
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            chain[i].Dispose();
        }
    }

    /// <summary>The outcome of <see cref="PinDirectoryChain"/>: either the whole open chain, root first, or ready-to-return error text.</summary>
    private readonly struct ChainResult
    {
        private ChainResult(List<PinnedDirectory>? chain, string? error)
        {
            Chain = chain;
            Error = error;
        }

        public List<PinnedDirectory>? Chain { get; }

        public string? Error { get; }

        public bool Ok => Error is null;

        public static ChainResult Success(List<PinnedDirectory> chain) => new(chain, null);

        public static ChainResult Failure(string error) => new(null, error);
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

        var (relativePath, result) = await OpenAndApplyEditAsync(caller, canonicalRoot, path, resolved, oldText, newText, ct).ConfigureAwait(false);
        if (relativePath is null)
        {
            return result;
        }

        Notify(workspace.RunId, relativePath);
        return result;
    }

    /// <summary>Pins the chain, opens the leaf and applies the edit for <see cref="EditFile"/>, once the pre-check has passed — split out only to keep <see cref="EditFile"/> under the method-length limit. Ruling (h): edit_file goes through the same pinned chain write_file does, opening only what already exists — never creating a missing parent directory as a side effect the way write_file's own chain would.</summary>
    private async Task<(string? RelativePath, string Result)> OpenAndApplyEditAsync(ISecurityContext caller, string canonicalRoot, string path, string resolved, string oldText, string newText, CancellationToken ct)
    {
        var deadline = new ContentionDeadline(options.ContentionTimeout);
        var chainResult = PinExistingDirectoryChain(canonicalRoot, Path.GetDirectoryName(resolved) ?? canonicalRoot, path);
        if (!chainResult.Ok)
        {
            return (null, chainResult.Error!);
        }

        var chain = chainResult.Chain!;
        try
        {
            using var lease = await LeafLocks.AcquireAsync(resolved, deadline.Remaining, ct).ConfigureAwait(false);
            if (lease is null)
            {
                return (null, BusyText);
            }

            var leafName = Path.GetFileName(resolved);
            var opened = await OpenLeafAsync(() => chain[^1].OpenExistingFile(leafName, readOnly: false, BeforeOpenForTesting), deadline, ct).ConfigureAwait(false);
            if (opened.IsFailure)
            {
                return (null, FormatLeafOpenFailure(opened.Error, path));
            }

            var file = opened.Value;
            try
            {
                return await ApplyEditAsync(caller, canonicalRoot, file, path, oldText, newText, ct).ConfigureAwait(false);
            }
            finally
            {
                file.Dispose();
            }
        }
        finally
        {
            DisposeChain(chain);
        }
    }

    /// <summary>
    /// The post-check, bounded read, exactly-once replacement and write for <c>edit_file</c>. Returns the changed
    /// path relative to the canonical root and the success text on success, or a null path and the refusal or error
    /// text otherwise — the caller notifies listeners only when the path is non-null.
    /// </summary>
    private async Task<(string? RelativePath, string Result)> ApplyEditAsync(
        ISecurityContext caller, string canonicalRoot, PinnedFile file, string path, string oldText, string newText, CancellationToken ct)
    {
        var post = CheckWrite(caller, canonicalRoot, file.RealPath, out var postExtension);
        if (post != WriteGate.Ok)
        {
            return (null, FormatGate(post, path, postExtension));
        }

        // Ruling (d): verified again, live, right before committing the edit — see PinnedFile.StillAtVerifiedPath.
        BeforeCommitForTesting?.Invoke();
        if (!file.StillAtVerifiedPath())
        {
            return (null, GenericRefusalText);
        }

        var read = await ReadBoundedAsync(file.Stream, path, ct).ConfigureAwait(false);
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

        file.Stream.Position = 0;
        file.Stream.SetLength(0);
        await file.Stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await file.Stream.FlushAsync(ct).ConfigureAwait(false);

        return (RelativeToRoot(canonicalRoot, file.RealPath), $"edited '{path}'.");
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
            return PathResolution.Failure(GenericRefusalText);
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
        WriteGate.NotPermitted => GenericRefusalText,
        WriteGate.Protected => Protected(path),
        WriteGate.Extension => extensionMessage!,
        _ => throw new InvalidOperationException($"{nameof(CheckWrite)} returned {nameof(WriteGate.Ok)}; there is nothing to format."),
    };

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

    /// <summary>
    /// Walks <paramref name="directory"/>'s own entries via its pinned handle — never re-resolving a name by path —
    /// skipping <c>.git</c> and any reparse point; a name found to be a subdirectory is descended into by opening it
    /// as a child of this same pinned directory, closing the gap between checking an entry's attributes and
    /// enumerating what a swap could have made it point to since. Stops once one more than <paramref name="limit"/>
    /// entries have been collected. Returns <see langword="false"/> on a genuine enumeration error at this level or
    /// any descendant, so <c>list_files</c> reports a failure rather than a silently truncated listing (ruling (e)).
    /// </summary>
    private bool Walk(PinnedDirectory directory, string relativePrefix, List<string> results, int limit, CancellationToken ct)
    {
        if (results.Count > limit)
        {
            return true;
        }

        ct.ThrowIfCancellationRequested();

        var enumerated = directory.EnumerateEntries();
        if (enumerated.IsFailure)
        {
            return false;
        }

        foreach (var entry in enumerated.Value)
        {
            if (results.Count > limit)
            {
                return true;
            }

            if (entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) || entry.IsReparsePoint)
            {
                continue;
            }

            var relative = relativePrefix.Length == 0 ? entry.Name : relativePrefix + "/" + entry.Name;
            if (!entry.IsDirectory)
            {
                results.Add(relative);
                continue;
            }

            results.Add(relative + "/");
            var child = directory.OpenChild(entry.Name, BeforeOpenForTesting);
            if (child.IsFailure)
            {
                continue; // gone, or no longer a plain directory, since it was listed a moment ago
            }

            try
            {
                if (!Walk(child.Value, relative, results, limit, ct))
                {
                    return false;
                }
            }
            finally
            {
                child.Value.Dispose();
            }
        }

        return true;
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

}
