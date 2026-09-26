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
/// is a symlink, rather than needing a separate check afterward. The real flag values are architecture-dependent —
/// x86_64 and the "generic" ABI most other architectures use, arm64 included, disagree on <c>O_DIRECTORY</c> and
/// <c>O_NOFOLLOW</c> specifically — so <see cref="OpenRoot"/> resolves them from <see cref="PinnedIo.FlagsFor"/>
/// and fails closed before any open on an architecture that table does not cover, rather than silently opening with
/// the wrong flags (round-3 finding A). Every level's real path, and the leaf file's, is verified the same way as
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
/// <b>Where pinning cannot run</b> — any platform other than Windows or Linux, or a Linux process architecture
/// <see cref="PinnedIo.FlagsFor"/> does not cover — <see cref="OpenRoot"/> fails closed.
/// </para>
/// </remarks>
internal sealed class PinnedDirectory : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly PinnedDirectory? _parent;
    private readonly string? _name;

    private PinnedDirectory(SafeFileHandle handle, string realPath, bool wasCreated, PinnedDirectory? parent, string? name)
    {
        _handle = handle;
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
    public static Result<PinnedDirectory, string> OpenRoot(string canonicalRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = PinnedIo.Windows.CreateFileW(canonicalRoot, PinnedIo.Windows.GenericRead, PinnedIo.Windows.FileShareRead | PinnedIo.Windows.FileShareWrite, PinnedIo.Windows.OpenExisting);
            return Verify(handle, canonicalRoot, wasCreated: false, parent: null, name: null);
        }

        if (OperatingSystem.IsLinux())
        {
            if (!PinnedIo.Linux.IsSupportedArchitecture)
            {
                // Ruling (a): fail closed before the first open, on the one architecture-dependent table every
                // Linux entry point below relies on, rather than opening with flag values that might mean something
                // else entirely on this process's architecture.
                return Result<PinnedDirectory, string>.Failure(WorkspaceTools.GenericRefusalText);
            }

            var handle = PinnedIo.Linux.Open(canonicalRoot, PinnedIo.Linux.ODirectory | PinnedIo.Linux.ONoFollow | PinnedIo.Linux.OCloExec);
            return Verify(handle, canonicalRoot, wasCreated: false, parent: null, name: null);
        }

        return Result<PinnedDirectory, string>.Failure(WorkspaceTools.GenericRefusalText);
    }

    /// <summary>Opens an existing child directory relative to this pinned one. Classifies a missing level separately from any other refusal, so a caller building a read-only chain (<c>read_file</c>, <c>edit_file</c>) can report "does not exist" the same way a missing leaf file does.</summary>
    /// <param name="name">The child's name.</param>
    /// <param name="beforeOpen">Test-only seam: invoked with the candidate path (Windows) or <paramref name="name"/> (Linux) immediately before the open — see <see cref="WorkspaceTools.BeforeOpenForTesting"/>.</param>
    public Result<PinnedDirectory, PinnedOpenOutcome> OpenChild(string name, Action<string>? beforeOpen = null)
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

            return VerifyClassified(handle, candidate, wasCreated: false, parent: this, name: name);
        }

        if (OperatingSystem.IsLinux())
        {
            beforeOpen?.Invoke(name);
            var handle = PinnedIo.Linux.OpenAt(_handle, name, PinnedIo.Linux.ODirectory | PinnedIo.Linux.ONoFollow | PinnedIo.Linux.OCloExec);
            if (handle.IsInvalid)
            {
                return Result<PinnedDirectory, PinnedOpenOutcome>.Failure(ClassifyLinuxError(Marshal.GetLastPInvokeError()));
            }

            return VerifyClassified(handle, Path.Combine(RealPath, name), wasCreated: false, parent: this, name: name);
        }

        return Result<PinnedDirectory, PinnedOpenOutcome>.Failure(PinnedOpenOutcome.Refused);
    }

    /// <summary>
    /// Creates <paramref name="name"/> if it is missing, then opens and verifies it the same way as
    /// <see cref="OpenChild"/>. On Windows, <c>CreateFileW</c> cannot create a directory, so this is a genuine
    /// two-step — <c>CreateDirectoryW</c>, then open and verify — never one call that skips the verification; on
    /// Linux, <c>mkdirat</c> then <c>openat</c>. A concurrent caller creating the same level a moment earlier is not
    /// a failure.
    /// </summary>
    public Result<PinnedDirectory, string> CreateChild(string name, Action<string>? beforeOpen = null)
    {
        if (OperatingSystem.IsWindows())
        {
            var candidate = Path.Combine(RealPath, name);
            beforeOpen?.Invoke(candidate);
            var created = PinnedIo.Windows.CreateDirectoryW(candidate); // a false return — already occupied, by a concurrent creator or otherwise — is resolved by the open-and-verify step below, but must not be recorded as this call's own creation
            var handle = PinnedIo.Windows.CreateFileW(candidate, PinnedIo.Windows.GenericRead, PinnedIo.Windows.FileShareRead | PinnedIo.Windows.FileShareWrite, PinnedIo.Windows.OpenExisting);
            return Verify(handle, candidate, created, parent: this, name: name);
        }

        if (OperatingSystem.IsLinux())
        {
            beforeOpen?.Invoke(name);
            var created = PinnedIo.Linux.MkDirAt(_handle, name);
            var handle = PinnedIo.Linux.OpenAt(_handle, name, PinnedIo.Linux.ODirectory | PinnedIo.Linux.ONoFollow | PinnedIo.Linux.OCloExec);
            return Verify(handle, Path.Combine(RealPath, name), created, parent: this, name: name);
        }

        return Result<PinnedDirectory, string>.Failure(WorkspaceTools.GenericRefusalText);
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
        var handle = PinnedIo.Linux.OpenAt(_handle, name, PinnedIo.Linux.ORdWr | PinnedIo.Linux.ONoFollow | PinnedIo.Linux.OCloExec);
        var createdNew = false;
        if (handle.IsInvalid)
        {
            handle = PinnedIo.Linux.OpenAt(_handle, name, PinnedIo.Linux.OCreat | PinnedIo.Linux.OExcl | PinnedIo.Linux.ONoFollow | PinnedIo.Linux.OCloExec | PinnedIo.Linux.ORdWr, 0x1B4 /* 0644 */);
            createdNew = !handle.IsInvalid;
            if (handle.IsInvalid && Marshal.GetLastPInvokeError() == PinnedIo.Linux.EExist)
            {
                // Another writer created it in the gap between our "does it exist" open and this O_CREAT|O_EXCL
                // one; fall back to opening what is there now, the same as the ordinary already-occupied path
                // above, instead of refusing a legitimate write (ruling (c) minor).
                handle = PinnedIo.Linux.OpenAt(_handle, name, PinnedIo.Linux.ORdWr | PinnedIo.Linux.ONoFollow | PinnedIo.Linux.OCloExec);
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
            beforeOpen?.Invoke(name);
            var flags = (readOnly ? PinnedIo.Linux.ORdOnly : PinnedIo.Linux.ORdWr) | PinnedIo.Linux.ONoFollow | PinnedIo.Linux.OCloExec;
            var handle = PinnedIo.Linux.OpenAt(_handle, name, flags);
            if (handle.IsInvalid)
            {
                return Result<PinnedFile, PinnedOpenOutcome>.Failure(ClassifyLinuxError(Marshal.GetLastPInvokeError()));
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

        return Result<PinnedFile, PinnedOpenOutcome>.Failure(PinnedOpenOutcome.Refused);
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
    public bool TryRemoveSelf()
    {
        if (_parent is null || _name is null)
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            var expected = Path.Combine(_parent.RealPath, _name);
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
            var removed = PinnedIo.Linux.UnlinkAt(_parent._handle, _name, removeDirectory: true);
            _handle.Dispose();
            return removed;
        }

        return false;
    }

    public void Dispose() => _handle.Dispose();

    private static Result<PinnedDirectory, string> Verify(SafeFileHandle handle, string expectedRealPath, bool wasCreated, PinnedDirectory? parent, string? name)
    {
        if (handle.IsInvalid)
        {
            return Result<PinnedDirectory, string>.Failure(WorkspaceTools.GenericRefusalText);
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var real = WorkspacePath.FinalPathOfHandle(handle);
        if (real is null || !string.Equals(real, expectedRealPath, comparison))
        {
            handle.Dispose();
            return Result<PinnedDirectory, string>.Failure(WorkspaceTools.GenericRefusalText);
        }

        return Result<PinnedDirectory, string>.Success(new PinnedDirectory(handle, real, wasCreated, parent, name));
    }

    private static Result<PinnedDirectory, PinnedOpenOutcome> VerifyClassified(SafeFileHandle handle, string expectedRealPath, bool wasCreated, PinnedDirectory? parent, string? name)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var real = WorkspacePath.FinalPathOfHandle(handle);
        if (real is null || !string.Equals(real, expectedRealPath, comparison))
        {
            handle.Dispose();
            return Result<PinnedDirectory, PinnedOpenOutcome>.Failure(PinnedOpenOutcome.Refused);
        }

        return Result<PinnedDirectory, PinnedOpenOutcome>.Success(new PinnedDirectory(handle, real, wasCreated, parent, name));
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
