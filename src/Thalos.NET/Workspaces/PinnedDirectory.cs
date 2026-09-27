using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using ZeroAlloc.Results;

namespace Thalos.Workspaces;

/// <summary>The outcome of a pinned open that did not return a handle: whether the target is simply missing, contended by another handle right now, or refused for any other reason (a policy check, a verification mismatch, an unsupported platform). Ruling (j): callers that can tell these apart report them differently — "does not exist" for <see cref="Missing"/>, a distinct "busy" result for <see cref="Contended"/> — everything else keeps the one generic refusal text, never a path, an errno, or a Win32 error code.</summary>
internal enum PinnedOpenOutcome
{
    Refused,
    Missing,
    Contended,
}

/// <summary>
/// One directory in a walk from a workspace's canonical root down to a target, held open for as long as the caller
/// needs it — so that, once pinned, no other actor can rename, delete or replace it with a link. Every subsequent
/// step — opening or creating the next level, opening or creating the leaf file, enumerating entries, removing
/// something this call itself created — goes through this handle or file descriptor, never a path string. This is
/// what closes the gap <c>WorkspaceTools</c>' own path-based directory creation and cleanup left open: those acted
/// on strings inside a tree an attacker's swap could change out from under them, between the check and the use.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windows.</b> Each level is opened via <c>CreateFileW</c> with <c>FILE_FLAG_BACKUP_SEMANTICS</c> and a share
/// mode that excludes <c>FILE_SHARE_DELETE</c>, so it cannot be renamed, deleted or replaced by a link while any of
/// these handles stays open. The resulting handle's real path — via <see cref="WorkspacePath.FinalPathOfHandle"/> —
/// is checked to equal the already-pinned parent's own real path plus the one raw name exactly, not merely "inside
/// the root": a reparse point redirects to some other real path, which this equality check catches regardless of
/// where that other path leads. A missing level is created and then opened and verified separately, since
/// <c>CreateFileW</c> cannot itself create a directory. <b>Ordinary pins never request <c>DELETE</c> access</b>
/// (round-3 finding B1): requesting it would mean every other concurrently open handle to the same directory — an
/// unrelated call also walking through it — must also grant <c>FILE_SHARE_DELETE</c>, which none of them do, by
/// design, so two legitimate concurrent pins of one directory would always collide. Omitting <c>DELETE</c> does not
/// weaken the anti-swap protection at all: that protection comes entirely from the share mode excluding
/// <c>FILE_SHARE_DELETE</c>, which every pin still sets, never from the access rights a pin itself requests.
/// </para>
/// <para>
/// <b>Linux.</b> Each level is opened via <c>openat</c> with <c>O_DIRECTORY | O_NOFOLLOW</c>, relative to the
/// already-pinned parent's file descriptor — resolved directly against that one specific inode, never by walking a
/// path string, so there is nothing for a swap to redirect. <c>O_NOFOLLOW</c> makes the open itself fail if the name
/// is a symlink, rather than needing a separate check afterward. The real flag values are architecture-dependent:
/// x86_64 uses the generic Linux values, <c>O_DIRECTORY</c> 0x10000 and <c>O_NOFOLLOW</c> 0x20000, and arm64
/// overrides them with 0x4000 and 0x8000. So <see cref="OpenRoot"/> resolves them once per chain from
/// <see cref="PinnedIo.FlagsFor"/>, every level of the chain carries them, and the chain fails closed before any open
/// on an architecture that table does not cover, rather than silently opening with the wrong flags (round-3 finding
/// A). Every level's real path, and the leaf file's, is verified the same way as
/// Windows — reading <c>/proc/self/fd</c> back via <see cref="WorkspacePath.FinalPathOfHandle"/> — never computed by
/// concatenating a name onto the parent's own already-verified path, which is not a verification at all (round-3
/// finding B3).
/// </para>
/// <para>
/// <b>Removal never uses a path it has not just verified.</b> A directory this call created is removed, innermost
/// first: on Windows, this call's own non-<c>DELETE</c> pin handle is closed first — a handle without
/// <c>FILE_SHARE_DELETE</c> blocks every other handle, including a second one of this call's own, from ever gaining
/// <c>DELETE</c> access while it stays open — then a fresh handle is opened by the same already-verified path,
/// re-verified the same exact-match way, and marked for POSIX-semantics deletion (<c>FILE_DISPOSITION_INFO_EX</c>),
/// which unlinks the name immediately rather than waiting for every handle to close the way the legacy disposition
/// does (round-3 minor: a parent otherwise stays non-empty, and cleanup above the deepest level stops). On Linux,
/// <c>unlinkat</c> with <c>AT_REMOVEDIR</c> against the pinned parent's descriptor already unlinks immediately,
/// regardless of any other open descriptor, so no fresh handle or POSIX-semantics flag is needed there. Either way,
/// this is never <see cref="Directory.Delete(string)"/> against a string a swap could have redirected since the
/// level was created. A sharing violation opening the fresh Windows delete handle — another pin, this call's own or
/// a concurrent caller's, still holds the level — is not an error: the level is left as it is, and the caller's own
/// result is unaffected (ruling (b)).
/// </para>
/// <para>
/// <b>Removal never races another call in this process (round-5 ruling (t)).</b> Because Linux unlinks an empty
/// directory whatever descriptors are open on it, a pin alone cannot stop another call's cleanup there, and on
/// Windows the cleanup's own <c>DELETE</c> handle makes a concurrent pin fail. So every level below the root is
/// counted in a <see cref="DirectoryLevelTable"/> from before it is opened or created until it is disposed, and a
/// removal runs only when this call's own count is the only one, with new pins of that level held back until it is
/// done. A holder outside the process is not in the table: when opening a level fails because such a holder has
/// it, or because it vanished, the caller rebuilds its chain from the root.
/// </para>
/// <para>
/// <b>Where pinning cannot run</b> — any platform other than Windows or Linux, or a Linux process architecture
/// <see cref="PinnedIo.FlagsFor"/> does not cover — <see cref="OpenRoot"/> fails closed.
/// </para>
/// </remarks>
internal sealed class PinnedDirectory : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly PinnedDirectory? _parent;
    private readonly string? _name;
    private readonly (int ODirectory, int ONoFollow) _linuxFlags;
    private readonly DirectoryLevelTable _levels;
    private readonly DirectoryLevelTable.Lease? _lease;

    private PinnedDirectory(SafeFileHandle handle, string realPath, bool wasCreated, PinnedDirectory? parent, string? name, (int ODirectory, int ONoFollow) linuxFlags, DirectoryLevelTable levels, DirectoryLevelTable.Lease? lease)
    {
        _handle = handle;
        _linuxFlags = linuxFlags;
        _levels = levels;
        _lease = lease;
        RealPath = realPath;
        WasCreated = wasCreated;
        _parent = parent;
        _name = name;
    }

    /// <summary>This directory's real path — verified against the kernel on both Windows and Linux, via <see cref="WorkspacePath.FinalPathOfHandle"/>, never computed by string concatenation. Used only for policy decisions (extension, protected-path); every filesystem operation goes through <see cref="Handle"/>, never this string.</summary>
    public string RealPath { get; }

    /// <summary>The open handle or file descriptor, exposed only so <see cref="PinnedFile"/> can remove a file it created relative to this directory's own descriptor on Linux; never used to build a path string.</summary>
    internal SafeFileHandle Handle => _handle;

    /// <summary>Whether this specific call created this level, rather than finding it already there.</summary>
    public bool WasCreated { get; }

    /// <summary>Opens the workspace's canonical root as the start of a chain, verified to equal <paramref name="canonicalRoot"/> exactly. Fails closed on a platform other than Windows or Linux, or on a Linux process architecture <see cref="PinnedIo.FlagsFor"/> does not cover.</summary>
    /// <param name="canonicalRoot">The workspace's canonical root.</param>
    /// <param name="levels">The table every level below the root is counted in while pinned (ruling (t)). The root itself is never removed, so it is not counted.</param>
    /// <param name="architectureOverride">Test-only seam: the architecture the Linux flag table is read for, in place of this process's own. See <see cref="WorkspaceTools.ArchitectureOverrideForTesting"/>. <see langword="null"/> in production.</param>
    public static Result<PinnedDirectory, string> OpenRoot(string canonicalRoot, DirectoryLevelTable levels, Architecture? architectureOverride = null)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = PinnedIo.Windows.CreateFileW(canonicalRoot, PinnedIo.Windows.GenericRead, PinnedIo.Windows.FileShareRead | PinnedIo.Windows.FileShareWrite, PinnedIo.Windows.OpenExisting);
            return VerifyRoot(handle, canonicalRoot, linuxFlags: default, levels);
        }

        if (OperatingSystem.IsLinux())
        {
            if (PinnedIo.FlagsFor(architectureOverride ?? RuntimeInformation.ProcessArchitecture) is not { } flags)
            {
                // Ruling (a): fail closed before the first open, on the one architecture-dependent table every
                // Linux entry point below relies on, rather than opening with flag values that might mean something
                // else entirely on this process's architecture.
                return Result<PinnedDirectory, string>.Failure(WorkspaceTools.GenericRefusalText);
            }

            var handle = PinnedIo.Linux.Open(canonicalRoot, flags.ODirectory | flags.ONoFollow | PinnedIo.Linux.OCloExec);
            return VerifyRoot(handle, canonicalRoot, flags, levels);
        }

        return Result<PinnedDirectory, string>.Failure(WorkspaceTools.GenericRefusalText);
    }

    /// <summary>Opens an existing child directory relative to this pinned one. Classifies a missing level separately from any other refusal, so a caller building a read-only chain (<c>read_file</c>, <c>edit_file</c>) can report "does not exist" the same way a missing leaf file does.</summary>
    /// <param name="name">The child's name.</param>
    /// <param name="beforeOpen">Test-only seam: invoked with the candidate path (Windows) or <paramref name="name"/> (Linux) immediately before the open — see <see cref="WorkspaceTools.BeforeOpenForTesting"/>.</param>
    /// <remarks>The level is counted in the chain's <see cref="DirectoryLevelTable"/> before it is opened, and stays counted until this child is disposed; see <see cref="TryRemoveSelf"/>.</remarks>
    public Result<PinnedDirectory, PinnedOpenOutcome> OpenChild(string name, Action<string>? beforeOpen = null)
    {
        var lease = _levels.Pin(Path.Combine(RealPath, name));
        var opened = OpenChildPinned(name, lease, beforeOpen);
        if (opened.IsFailure)
        {
            lease.Dispose();
        }

        return opened;
    }

    private Result<PinnedDirectory, PinnedOpenOutcome> OpenChildPinned(string name, DirectoryLevelTable.Lease lease, Action<string>? beforeOpen)
    {
        if (OperatingSystem.IsWindows())
        {
            var candidate = Path.Combine(RealPath, name);
            beforeOpen?.Invoke(candidate);
            var handle = PinnedIo.Windows.CreateFileW(candidate, PinnedIo.Windows.GenericRead, PinnedIo.Windows.FileShareRead | PinnedIo.Windows.FileShareWrite, PinnedIo.Windows.OpenExisting);
            if (handle.IsInvalid)
            {
                return Result<PinnedDirectory, PinnedOpenOutcome>.Failure(ClassifyWindowsError(Marshal.GetLastPInvokeError()));
            }

            return Verify(handle, candidate, wasCreated: false, name, lease);
        }

        if (OperatingSystem.IsLinux())
        {
            beforeOpen?.Invoke(name);
            var handle = PinnedIo.Linux.OpenAt(_handle, name, _linuxFlags.ODirectory | _linuxFlags.ONoFollow | PinnedIo.Linux.OCloExec);
            if (handle.IsInvalid)
            {
                return Result<PinnedDirectory, PinnedOpenOutcome>.Failure(ClassifyLinuxError(Marshal.GetLastPInvokeError()));
            }

            return Verify(handle, Path.Combine(RealPath, name), wasCreated: false, name, lease);
        }

        return Result<PinnedDirectory, PinnedOpenOutcome>.Failure(PinnedOpenOutcome.Refused);
    }

    /// <summary>
    /// Creates <paramref name="name"/> if it is missing, then opens and verifies it the same way as
    /// <see cref="OpenChild"/>. On Windows, <c>CreateFileW</c> cannot create a directory, so this is a genuine
    /// two-step — <c>CreateDirectoryW</c>, then open and verify — never one call that skips the verification; on
    /// Linux, <c>mkdirat</c> then <c>openat</c>. A concurrent caller creating the same level a moment earlier is not
    /// a failure. A failed open is classified the same way <see cref="OpenChild"/> classifies one:
    /// <see cref="PinnedOpenOutcome.Missing"/> when the level is gone again by the time it is opened, and
    /// <see cref="PinnedOpenOutcome.Contended"/> for a sharing violation, so the caller can rebuild its chain from the
    /// root instead of refusing (ruling (t)). A failed verification is always <see cref="PinnedOpenOutcome.Refused"/>.
    /// </summary>
    /// <remarks>The level is counted in the chain's <see cref="DirectoryLevelTable"/> before it is created or opened, so a removal of it by another call in this process either finishes first or does not happen; see <see cref="TryRemoveSelf"/>.</remarks>
    public Result<PinnedDirectory, PinnedOpenOutcome> CreateChild(string name, Action<string>? beforeOpen = null)
    {
        var lease = _levels.Pin(Path.Combine(RealPath, name));
        var created = CreateChildPinned(name, lease, beforeOpen);
        if (created.IsFailure)
        {
            lease.Dispose();
        }

        return created;
    }

    private Result<PinnedDirectory, PinnedOpenOutcome> CreateChildPinned(string name, DirectoryLevelTable.Lease lease, Action<string>? beforeOpen)
    {
        if (OperatingSystem.IsWindows())
        {
            var candidate = Path.Combine(RealPath, name);
            beforeOpen?.Invoke(candidate);
            var created = PinnedIo.Windows.CreateDirectoryW(candidate); // a false return — already occupied, by a concurrent creator or otherwise — is resolved by the open-and-verify step below, but must not be recorded as this call's own creation
            var handle = PinnedIo.Windows.CreateFileW(candidate, PinnedIo.Windows.GenericRead, PinnedIo.Windows.FileShareRead | PinnedIo.Windows.FileShareWrite, PinnedIo.Windows.OpenExisting);
            if (handle.IsInvalid)
            {
                return Result<PinnedDirectory, PinnedOpenOutcome>.Failure(ClassifyWindowsError(Marshal.GetLastPInvokeError()));
            }

            return Verify(handle, candidate, created, name, lease);
        }

        if (OperatingSystem.IsLinux())
        {
            beforeOpen?.Invoke(name);
            var created = PinnedIo.Linux.MkDirAt(_handle, name);
            var handle = PinnedIo.Linux.OpenAt(_handle, name, _linuxFlags.ODirectory | _linuxFlags.ONoFollow | PinnedIo.Linux.OCloExec);
            if (handle.IsInvalid)
            {
                return Result<PinnedDirectory, PinnedOpenOutcome>.Failure(ClassifyLinuxError(Marshal.GetLastPInvokeError()));
            }

            var verified = Verify(handle, Path.Combine(RealPath, name), created, name, lease);
            if (verified.IsFailure && created)
            {
                // Ruling (n): mkdirat created the level relative to this directory's descriptor, which still refers
                // to this directory wherever a rename has since moved it, so the new level can sit outside the
                // workspace. Remove it through the same descriptor. AT_REMOVEDIR removes only an empty directory,
                // never a file or a link.
                PinnedIo.Linux.UnlinkAt(_handle, name, removeDirectory: true);
            }

            return verified;
        }

        return Result<PinnedDirectory, PinnedOpenOutcome>.Failure(PinnedOpenOutcome.Refused);
    }

    /// <summary>Opens <paramref name="name"/> relative to this pinned directory if it exists, otherwise creates it — <see cref="FileMode.Open"/> falling back to <see cref="FileMode.CreateNew"/>, exactly as before, but relative to an already-pinned parent rather than a path string built from an unpinned tree. Never opens a directory as if it were a plain file (ruling (f)). A failed open is classified: a genuine sharing conflict — this call's own now-exclusive open contending with something that already holds the name — is reported distinctly from an ordinary refusal (ruling (j)), since <em>this</em> method's own retry-or-refuse decision belongs to the caller, not here.</summary>
    public Result<PinnedFile, PinnedOpenOutcome> OpenOrCreateFile(string name, Action<string>? beforeOpen = null)
    {
        if (OperatingSystem.IsWindows())
        {
            return OpenOrCreateFileWindows(name, beforeOpen);
        }

        if (OperatingSystem.IsLinux())
        {
            return OpenOrCreateFileLinux(name, beforeOpen);
        }

        return Result<PinnedFile, PinnedOpenOutcome>.Failure(PinnedOpenOutcome.Refused);
    }

    [SupportedOSPlatform("windows")]
    private Result<PinnedFile, PinnedOpenOutcome> OpenOrCreateFileWindows(string name, Action<string>? beforeOpen)
    {
        var candidate = Path.Combine(RealPath, name);
        beforeOpen?.Invoke(candidate);
        var existing = PinnedIo.Windows.CreateFileW(candidate, PinnedIo.Windows.GenericRead | PinnedIo.Windows.GenericWrite | PinnedIo.Windows.Delete, 0, PinnedIo.Windows.OpenExisting);
        var handle = existing;
        var createdNew = false;
        var lastError = 0;
        if (existing.IsInvalid)
        {
            var existingError = Marshal.GetLastPInvokeError();
            handle = PinnedIo.Windows.CreateFileW(candidate, PinnedIo.Windows.GenericRead | PinnedIo.Windows.GenericWrite | PinnedIo.Windows.Delete, 0, PinnedIo.Windows.CreateNewDisposition);
            createdNew = !handle.IsInvalid;
            if (handle.IsInvalid)
            {
                var createError = Marshal.GetLastPInvokeError();

                // The file exists — that is WHY CreateNew just failed with ERROR_FILE_EXISTS/ERROR_ALREADY_EXISTS —
                // so that is an expected, uninformative side effect of trying the fallback anyway, never the real
                // cause. The real cause is whatever made the "open existing" attempt itself fail, which classifying
                // by createError alone would otherwise discard (e.g. a genuine sharing violation reads as a
                // generic refusal instead of contention).
                lastError = createError is (int)PinnedIo.Windows.ErrorFileExists or (int)PinnedIo.Windows.ErrorAlreadyExists
                    ? existingError
                    : createError;
            }
        }

        if (handle.IsInvalid)
        {
            return Result<PinnedFile, PinnedOpenOutcome>.Failure(PinnedIo.Windows.IsContention(lastError) ? PinnedOpenOutcome.Contended : PinnedOpenOutcome.Refused);
        }

        if (PinnedIo.Windows.IsDirectory(handle))
        {
            handle.Dispose();
            return Result<PinnedFile, PinnedOpenOutcome>.Failure(PinnedOpenOutcome.Refused);
        }

        var real = WorkspacePath.FinalPathOfHandle(handle);
        if (real is null || !string.Equals(real, candidate, StringComparison.OrdinalIgnoreCase))
        {
            // Symmetric with the Linux branch: this handle already carries DELETE access, so a file this call just
            // created — exclusive by construction, via CREATE_NEW — can be marked for removal by the same handle
            // before disposing it, rather than left behind wherever an ancestor rename since put it.
            if (createdNew)
            {
                PinnedIo.Windows.MarkForDeletionPosix(handle);
            }

            handle.Dispose();
            return Result<PinnedFile, PinnedOpenOutcome>.Failure(PinnedOpenOutcome.Refused);
        }

        return Result<PinnedFile, PinnedOpenOutcome>.Success(new PinnedFile(new FileStream(handle, FileAccess.ReadWrite), real, createdNew, this, name));
    }

    [SupportedOSPlatform("linux")]
    private Result<PinnedFile, PinnedOpenOutcome> OpenOrCreateFileLinux(string name, Action<string>? beforeOpen)
    {
        beforeOpen?.Invoke(name);
        var handle = PinnedIo.Linux.OpenAt(_handle, name, PinnedIo.Linux.ORdWr | _linuxFlags.ONoFollow | PinnedIo.Linux.OCloExec);
        var createdNew = false;
        if (handle.IsInvalid)
        {
            handle = PinnedIo.Linux.OpenAt(_handle, name, PinnedIo.Linux.OCreat | PinnedIo.Linux.OExcl | _linuxFlags.ONoFollow | PinnedIo.Linux.OCloExec | PinnedIo.Linux.ORdWr, 0x1B4 /* 0644 */);
            createdNew = !handle.IsInvalid;
            if (handle.IsInvalid && Marshal.GetLastPInvokeError() == PinnedIo.Linux.EExist)
            {
                // Another writer created it in the gap between our "does it exist" open and this O_CREAT|O_EXCL
                // one; fall back to opening what is there now, the same as the ordinary already-occupied path
                // above, instead of refusing a legitimate write (ruling (c) minor).
                handle = PinnedIo.Linux.OpenAt(_handle, name, PinnedIo.Linux.ORdWr | _linuxFlags.ONoFollow | PinnedIo.Linux.OCloExec);
                createdNew = false;
            }
        }

        if (handle.IsInvalid)
        {
            // OpenOrCreateFile always attempts to create a missing target, so "missing" cannot happen here — an
            // open failure at this point means either genuine contention (ruling (j)) or something else.
            var outcome = ClassifyLinuxError(Marshal.GetLastPInvokeError());
            return Result<PinnedFile, PinnedOpenOutcome>.Failure(outcome == PinnedOpenOutcome.Missing ? PinnedOpenOutcome.Refused : outcome);
        }

        var expected = Path.Combine(RealPath, name);
        var real = WorkspacePath.FinalPathOfHandle(handle);
        if (real is null || !string.Equals(real, expected, StringComparison.Ordinal))
        {
            // The file itself is exactly what O_CREAT|O_EXCL just created — exclusive by construction, so nothing
            // else could have swapped its identity — but an ancestor (this directory itself, or one further up)
            // can have been renamed away since. If this call created it, remove it via this same fd-relative
            // unlinkat, immune to that rename the same way the open above was, rather than leaving an orphan
            // behind wherever the rename put it.
            if (createdNew)
            {
                PinnedIo.Linux.UnlinkAt(_handle, name, removeDirectory: false);
            }

            handle.Dispose();
            return Result<PinnedFile, PinnedOpenOutcome>.Failure(PinnedOpenOutcome.Refused);
        }

        return Result<PinnedFile, PinnedOpenOutcome>.Success(new PinnedFile(new FileStream(handle, FileAccess.ReadWrite), real, createdNew, this, name));
    }

    /// <summary>Opens <paramref name="name"/> relative to this pinned directory if it already exists; never creates one. Used by <c>read_file</c> and <c>edit_file</c> (ruling (h)), which — unlike <c>write_file</c> — must not bring a missing file, or a missing parent directory, into existence as a side effect. Classifies a missing target from any other refusal the same way <see cref="OpenChild"/> does, and never opens a directory as if it were a plain file (ruling (f)).</summary>
    public Result<PinnedFile, PinnedOpenOutcome> OpenExistingFile(string name, bool readOnly, Action<string>? beforeOpen = null)
    {
        if (OperatingSystem.IsWindows())
        {
            var candidate = Path.Combine(RealPath, name);
            beforeOpen?.Invoke(candidate);
            var access = readOnly ? PinnedIo.Windows.GenericRead : PinnedIo.Windows.GenericRead | PinnedIo.Windows.GenericWrite;
            var share = readOnly ? PinnedIo.Windows.FileShareRead | PinnedIo.Windows.FileShareWrite : 0u;
            var handle = PinnedIo.Windows.CreateFileW(candidate, access, share, PinnedIo.Windows.OpenExisting);
            if (handle.IsInvalid)
            {
                return Result<PinnedFile, PinnedOpenOutcome>.Failure(ClassifyWindowsError(Marshal.GetLastPInvokeError()));
            }

            if (PinnedIo.Windows.IsDirectory(handle))
            {
                handle.Dispose();
                return Result<PinnedFile, PinnedOpenOutcome>.Failure(PinnedOpenOutcome.Refused);
            }

            var real = WorkspacePath.FinalPathOfHandle(handle);
            if (real is null || !string.Equals(real, candidate, StringComparison.OrdinalIgnoreCase))
            {
                handle.Dispose();
                return Result<PinnedFile, PinnedOpenOutcome>.Failure(PinnedOpenOutcome.Refused);
            }

            var mode = readOnly ? FileAccess.Read : FileAccess.ReadWrite;
            return Result<PinnedFile, PinnedOpenOutcome>.Success(new PinnedFile(new FileStream(handle, mode), real, createdNew: false, this, name));
        }

        if (OperatingSystem.IsLinux())
        {
            return OpenExistingFileLinux(name, readOnly, beforeOpen);
        }

        return Result<PinnedFile, PinnedOpenOutcome>.Failure(PinnedOpenOutcome.Refused);
    }

    [SupportedOSPlatform("linux")]
    private Result<PinnedFile, PinnedOpenOutcome> OpenExistingFileLinux(string name, bool readOnly, Action<string>? beforeOpen)
    {
        beforeOpen?.Invoke(name);
        var flags = (readOnly ? PinnedIo.Linux.ORdOnly : PinnedIo.Linux.ORdWr) | _linuxFlags.ONoFollow | PinnedIo.Linux.OCloExec;
        var handle = PinnedIo.Linux.OpenAt(_handle, name, flags);
        if (handle.IsInvalid)
        {
            return Result<PinnedFile, PinnedOpenOutcome>.Failure(ClassifyLinuxError(Marshal.GetLastPInvokeError()));
        }

        if (PinnedIo.Linux.IsDirectory(handle))
        {
            // Ruling (f): a read-only open of a directory succeeds on Linux, so refuse it here, before the handle
            // reaches a FileStream whose first read would throw.
            handle.Dispose();
            return Result<PinnedFile, PinnedOpenOutcome>.Failure(PinnedOpenOutcome.Refused);
        }

        var expected = Path.Combine(RealPath, name);
        var real = WorkspacePath.FinalPathOfHandle(handle);
        if (real is null || !string.Equals(real, expected, StringComparison.Ordinal))
        {
            handle.Dispose();
            return Result<PinnedFile, PinnedOpenOutcome>.Failure(PinnedOpenOutcome.Refused);
        }

        var mode = readOnly ? FileAccess.Read : FileAccess.ReadWrite;
        return Result<PinnedFile, PinnedOpenOutcome>.Success(new PinnedFile(new FileStream(handle, mode), real, createdNew: false, this, name));
    }

    /// <summary>Enumerates this directory's own entries via its open handle — never by path, and never requesting <c>DELETE</c> access to get here (ruling (e); round-3 finding B2) — so a swap between checking a name's attributes and listing it cannot substitute an outside directory's contents, and a concurrent pin of the same directory by another call never collides with this one. A genuine enumeration error is distinguished from ordinary exhaustion and returned as a failure, never silently truncated (ruling (e)).</summary>
    public Result<List<PinnedDirEntry>, string> EnumerateEntries()
    {
        if (OperatingSystem.IsWindows())
        {
            var (raw, ok) = PinnedIo.Windows.EnumerateDirectory(_handle);
            if (!ok)
            {
                return Result<List<PinnedDirEntry>, string>.Failure(WorkspaceTools.GenericRefusalText);
            }

            var entries = new List<PinnedDirEntry>(raw.Count);
            foreach (var (name, attributes) in raw)
            {
                const uint reparsePoint = 0x400;
                const uint directory = 0x10;
                entries.Add(new PinnedDirEntry(name, (attributes & directory) != 0, (attributes & reparsePoint) != 0));
            }

            return Result<List<PinnedDirEntry>, string>.Success(entries);
        }

        if (OperatingSystem.IsLinux())
        {
            var (entries, ok) = PinnedIo.Linux.EnumerateDirectory(_handle);
            return ok
                ? Result<List<PinnedDirEntry>, string>.Success(entries)
                : Result<List<PinnedDirEntry>, string>.Failure(WorkspaceTools.GenericRefusalText);
        }

        return Result<List<PinnedDirEntry>, string>.Success([]);
    }

    /// <summary>
    /// Removes this directory — only valid for one this call created, and only while it is empty — never by
    /// re-resolving a path a swap could have redirected since it was created. On Windows: this call's own pin
    /// handle, which never carries <c>DELETE</c> access (ruling (b)), is closed first, since no handle can gain
    /// <c>DELETE</c> access to this object while any handle without <c>FILE_SHARE_DELETE</c> — including this one —
    /// stays open; a fresh handle is then opened by the same already-verified path, re-verified the same exact-match
    /// way, and marked for POSIX-semantics deletion. A sharing violation opening that fresh handle — another pin
    /// still holds the level — is left as it is, not an error: <see langword="false"/> here only tells the caller's
    /// own loop to stop trying ancestors, which are not empty either way. On Linux: <c>unlinkat</c> against the
    /// parent's descriptor, which needs no such dance, since Linux unlinks immediately regardless of other open
    /// descriptors.
    /// </summary>
    /// <remarks>
    /// Ruling (t): the removal runs only through <see cref="DirectoryLevelTable.TryRemove"/>, so it happens only when
    /// no other chain in this process holds the level, and no new pin of the level opens or creates it until the
    /// removal is finished. Another chain that holds the level makes this return <see langword="false"/> without
    /// touching it, the same as a sharing violation from a holder outside the process: the level is left for that
    /// holder, and this call's own result is unaffected. A successful removal drops this level's own count at once,
    /// so a later call that creates the level anew can remove it again.
    /// </remarks>
    public bool TryRemoveSelf()
    {
        if (_parent is null || _name is null || _lease is null)
        {
            return false;
        }

        var removed = _levels.TryRemove(_lease, RemoveSelfUnderTable);
        if (removed)
        {
            _lease.Dispose();
        }

        return removed;
    }

    /// <summary>The removal itself, run by <see cref="DirectoryLevelTable.TryRemove"/> while it holds this level's removal lock. Only called for a level with a parent and a name.</summary>
    private bool RemoveSelfUnderTable()
    {
        var parent = _parent!;
        var name = _name!;
        if (OperatingSystem.IsWindows())
        {
            var expected = Path.Combine(parent.RealPath, name);
            _handle.Dispose();
            var deleteHandle = PinnedIo.Windows.CreateFileW(expected, PinnedIo.Windows.Delete, PinnedIo.Windows.FileShareRead | PinnedIo.Windows.FileShareWrite, PinnedIo.Windows.OpenExisting);
            if (deleteHandle.IsInvalid)
            {
                return false;
            }

            try
            {
                var real = WorkspacePath.FinalPathOfHandle(deleteHandle);
                return real is not null
                    && string.Equals(real, expected, StringComparison.OrdinalIgnoreCase)
                    && PinnedIo.Windows.MarkForDeletionPosix(deleteHandle);
            }
            finally
            {
                deleteHandle.Dispose();
            }
        }

        if (OperatingSystem.IsLinux())
        {
            var removed = PinnedIo.Linux.UnlinkAt(parent._handle, name, removeDirectory: true);
            _handle.Dispose();
            return removed;
        }

        return false;
    }

    /// <summary>Closes the handle or descriptor and drops this level's count in the <see cref="DirectoryLevelTable"/>. Safe to call more than once.</summary>
    public void Dispose()
    {
        _handle.Dispose();
        _lease?.Dispose();
    }

    private static Result<PinnedDirectory, string> VerifyRoot(SafeFileHandle handle, string canonicalRoot, (int ODirectory, int ONoFollow) linuxFlags, DirectoryLevelTable levels)
    {
        if (handle.IsInvalid)
        {
            return Result<PinnedDirectory, string>.Failure(WorkspaceTools.GenericRefusalText);
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var real = WorkspacePath.FinalPathOfHandle(handle);
        if (real is null || !string.Equals(real, canonicalRoot, comparison))
        {
            handle.Dispose();
            return Result<PinnedDirectory, string>.Failure(WorkspaceTools.GenericRefusalText);
        }

        return Result<PinnedDirectory, string>.Success(new PinnedDirectory(handle, real, wasCreated: false, parent: null, name: null, linuxFlags, levels, lease: null));
    }

    /// <summary>Verifies a child level's open handle against <paramref name="expectedRealPath"/>, exactly. The caller has already turned an invalid handle into a classified failure; a mismatch here is always <see cref="PinnedOpenOutcome.Refused"/>. On success the child carries <paramref name="lease"/>, which it releases when disposed.</summary>
    private Result<PinnedDirectory, PinnedOpenOutcome> Verify(SafeFileHandle handle, string expectedRealPath, bool wasCreated, string name, DirectoryLevelTable.Lease lease)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var real = WorkspacePath.FinalPathOfHandle(handle);
        if (real is null || !string.Equals(real, expectedRealPath, comparison))
        {
            handle.Dispose();
            return Result<PinnedDirectory, PinnedOpenOutcome>.Failure(PinnedOpenOutcome.Refused);
        }

        return Result<PinnedDirectory, PinnedOpenOutcome>.Success(new PinnedDirectory(handle, real, wasCreated, this, name, _linuxFlags, _levels, lease));
    }

    [SupportedOSPlatform("windows")]
    private static PinnedOpenOutcome ClassifyWindowsError(int win32Error) =>
        win32Error is (int)PinnedIo.Windows.ErrorFileNotFound or (int)PinnedIo.Windows.ErrorPathNotFound ? PinnedOpenOutcome.Missing
        : PinnedIo.Windows.IsContention(win32Error) ? PinnedOpenOutcome.Contended
        : PinnedOpenOutcome.Refused;

    [SupportedOSPlatform("linux")]
    private static PinnedOpenOutcome ClassifyLinuxError(int errno) =>
        errno == PinnedIo.Linux.ENoEnt ? PinnedOpenOutcome.Missing
        : PinnedIo.Linux.IsContention(errno) ? PinnedOpenOutcome.Contended
        : PinnedOpenOutcome.Refused;
}

/// <summary>An open leaf file, together with whether this call created it, and enough of its pinned parent to remove it later by handle or descriptor rather than by path — see <see cref="PinnedDirectory.OpenOrCreateFile"/>.</summary>
internal sealed class PinnedFile : IDisposable
{
    private readonly PinnedDirectory _parent;
    private readonly string _name;

    public PinnedFile(FileStream stream, string realPath, bool createdNew, PinnedDirectory parent, string name)
    {
        Stream = stream;
        RealPath = realPath;
        CreatedNew = createdNew;
        _parent = parent;
        _name = name;
    }

    public FileStream Stream { get; }

    /// <summary>This file's real path, verified once via <see cref="WorkspacePath.FinalPathOfHandle"/> at open time — on Linux this reads <c>/proc/self/fd</c> back, never a computed <c>Path.Combine</c> of the parent's real path and this file's name (round-3 finding B3). See <see cref="StillAtVerifiedPath"/> for the second, live re-check immediately before commit that ruling (d) also requires.</summary>
    public string RealPath { get; }

    public bool CreatedNew { get; }

    /// <summary>Re-reads this file's real path live from the kernel — via the same <see cref="WorkspacePath.FinalPathOfHandle"/> call <see cref="RealPath"/> was verified with at open time — and compares it against that already-verified value, rather than trusting <see cref="RealPath"/> to still be accurate. Ruling (d): every Linux level and the leaf file are verified "after open and again before commit"; this is the second check, called immediately before the write, closing the gap between the open-time verification and the moment content is actually written.</summary>
    public bool StillAtVerifiedPath()
    {
        var live = WorkspacePath.FinalPathOfHandle(Stream.SafeFileHandle);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return live is not null && string.Equals(live, RealPath, comparison);
    }

    /// <summary>Removes this file via its pinned parent's descriptor (Linux) or by marking its own handle for POSIX-semantics deletion (Windows, unlinking the name immediately rather than deferring to the handle's own close) — never by path.</summary>
    public bool TryRemove()
    {
        if (OperatingSystem.IsWindows())
        {
            return PinnedIo.Windows.MarkForDeletionPosix(Stream.SafeFileHandle);
        }

        if (OperatingSystem.IsLinux())
        {
            return PinnedIo.Linux.UnlinkAt(GetParentHandle(), _name, removeDirectory: false);
        }

        return false;
    }

    private SafeFileHandle GetParentHandle() => _parent.Handle;

    public void Dispose() => Stream.Dispose();
}

/// <summary>One entry from <see cref="PinnedDirectory.EnumerateEntries"/>: a name, known directly from the same handle-based call that names it, never from a follow-up, separately-resolved query.</summary>
internal readonly record struct PinnedDirEntry(string Name, bool IsDirectory, bool IsReparsePoint);
