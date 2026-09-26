using Microsoft.Win32.SafeHandles;
using ZeroAlloc.Results;

namespace Thalos.Workspaces;

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
/// where that other path leads. A missing level is created and opened as one atomic <c>CreateFileW</c> call
/// (<c>CREATE_NEW</c> with <c>FILE_ATTRIBUTE_DIRECTORY</c>), falling back to <c>OPEN_EXISTING</c> only when
/// <c>CREATE_NEW</c> reports the name already occupied — a concurrent caller may have created the very same level a
/// moment earlier, which must succeed cleanly, never surface as an exception.
/// </para>
/// <para>
/// <b>Linux.</b> Each level is opened via <c>openat</c> with <c>O_DIRECTORY | O_NOFOLLOW</c>, relative to the
/// already-pinned parent's file descriptor — resolved directly against that one specific inode, never by walking a
/// path string, so there is nothing for a swap to redirect. <c>O_NOFOLLOW</c> makes the open itself fail if the name
/// is a symlink, rather than needing a separate check afterward. A missing level is created with <c>mkdirat</c>,
/// then opened the same way; <c>mkdirat</c> failing with <c>EEXIST</c> falls back to opening what is already there,
/// for the same concurrent-creation reason as Windows.
/// </para>
/// <para>
/// <b>Removal never uses a path.</b> A directory this call created is removed, innermost first, by marking its own
/// handle for deletion on Windows (<c>SetFileInformationByHandle</c> with <c>FileDispositionInfo</c>, which checks
/// emptiness immediately) or by <c>unlinkat</c> with <c>AT_REMOVEDIR</c> against its pinned parent's descriptor on
/// Linux — never <see cref="Directory.Delete(string)"/> against a string that a swap could have redirected outside
/// the workspace since the level was created.
/// </para>
/// <para>
/// <b>Where pinning cannot run</b> — any platform other than Windows or Linux — <see cref="OpenRoot"/> fails closed.
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

    /// <summary>This directory's real path — verified against the kernel on Windows, computed from a chain of fd-relative opens on Linux, where no separate verification is needed. Used only for policy decisions (extension, protected-path); every filesystem operation goes through <see cref="Handle"/>, never this string.</summary>
    public string RealPath { get; }

    /// <summary>The open handle or file descriptor, exposed only so <see cref="PinnedFile"/> can remove a file it created relative to this directory's own descriptor on Linux; never used to build a path string.</summary>
    internal SafeFileHandle Handle => _handle;

    /// <summary>Whether this specific call created this level, rather than finding it already there.</summary>
    public bool WasCreated { get; }

    /// <summary>Opens the workspace's canonical root as the start of a chain, verified to equal <paramref name="canonicalRoot"/> exactly. Fails closed on a platform other than Windows or Linux.</summary>
    public static Result<PinnedDirectory, string> OpenRoot(string canonicalRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = PinnedIo.Windows.CreateFileW(canonicalRoot, PinnedIo.Windows.GenericRead, PinnedIo.Windows.FileShareRead | PinnedIo.Windows.FileShareWrite, PinnedIo.Windows.OpenExisting);
            return VerifyWindows(handle, canonicalRoot, wasCreated: false, parent: null, name: null);
        }

        if (OperatingSystem.IsLinux())
        {
            var handle = PinnedIo.Linux.Open(canonicalRoot, PinnedIo.Linux.ODirectory | PinnedIo.Linux.ONoFollow | PinnedIo.Linux.OCloExec);
            return VerifyLinuxRoot(handle, canonicalRoot);
        }

        return Result<PinnedDirectory, string>.Failure(WorkspaceTools.GenericRefusalText);
    }

    /// <summary>Opens an existing child directory relative to this pinned one. Fails if it is missing, not a directory, or a link.</summary>
    /// <param name="name">The child's name.</param>
    /// <param name="beforeOpen">Test-only seam: invoked with the candidate path (Windows) or <paramref name="name"/> (Linux) immediately before the open — see <see cref="WorkspaceTools.BeforeOpenForTesting"/>.</param>
    public Result<PinnedDirectory, string> OpenChild(string name, Action<string>? beforeOpen = null)
    {
        if (OperatingSystem.IsWindows())
        {
            var candidate = Path.Combine(RealPath, name);
            beforeOpen?.Invoke(candidate);
            var handle = PinnedIo.Windows.CreateFileW(candidate, PinnedIo.Windows.GenericRead | PinnedIo.Windows.Delete, PinnedIo.Windows.FileShareRead | PinnedIo.Windows.FileShareWrite, PinnedIo.Windows.OpenExisting);
            return VerifyWindows(handle, candidate, wasCreated: false, parent: this, name: name);
        }

        if (OperatingSystem.IsLinux())
        {
            beforeOpen?.Invoke(name);
            var handle = PinnedIo.Linux.OpenAt(_handle, name, PinnedIo.Linux.ODirectory | PinnedIo.Linux.ONoFollow | PinnedIo.Linux.OCloExec);
            return handle.IsInvalid
                ? Result<PinnedDirectory, string>.Failure(WorkspaceTools.GenericRefusalText)
                : Result<PinnedDirectory, string>.Success(new PinnedDirectory(handle, Path.Combine(RealPath, name), wasCreated: false, this, name));
        }

        return Result<PinnedDirectory, string>.Failure(WorkspaceTools.GenericRefusalText);
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
            var handle = PinnedIo.Windows.CreateFileW(candidate, PinnedIo.Windows.GenericRead | PinnedIo.Windows.Delete, PinnedIo.Windows.FileShareRead | PinnedIo.Windows.FileShareWrite, PinnedIo.Windows.OpenExisting);
            return VerifyWindows(handle, candidate, created, parent: this, name: name);
        }

        if (OperatingSystem.IsLinux())
        {
            beforeOpen?.Invoke(name);
            var created = PinnedIo.Linux.MkDirAt(_handle, name);
            var handle = PinnedIo.Linux.OpenAt(_handle, name, PinnedIo.Linux.ODirectory | PinnedIo.Linux.ONoFollow | PinnedIo.Linux.OCloExec);
            return handle.IsInvalid
                ? Result<PinnedDirectory, string>.Failure(WorkspaceTools.GenericRefusalText)
                : Result<PinnedDirectory, string>.Success(new PinnedDirectory(handle, Path.Combine(RealPath, name), created, this, name));
        }

        return Result<PinnedDirectory, string>.Failure(WorkspaceTools.GenericRefusalText);
    }

    /// <summary>Opens <paramref name="name"/> relative to this pinned directory if it exists, otherwise creates it — <see cref="FileMode.Open"/> falling back to <see cref="FileMode.CreateNew"/>, exactly as before, but relative to an already-pinned parent rather than a path string built from an unpinned tree.</summary>
    public Result<PinnedFile, string> OpenOrCreateFile(string name, Action<string>? beforeOpen = null)
    {
        if (OperatingSystem.IsWindows())
        {
            var candidate = Path.Combine(RealPath, name);
            beforeOpen?.Invoke(candidate);
            var existing = PinnedIo.Windows.CreateFileW(candidate, PinnedIo.Windows.GenericRead | PinnedIo.Windows.GenericWrite | PinnedIo.Windows.Delete, 0, PinnedIo.Windows.OpenExisting);
            var createdNew = false;
            var handle = existing;
            if (existing.IsInvalid)
            {
                handle = PinnedIo.Windows.CreateFileW(candidate, PinnedIo.Windows.GenericRead | PinnedIo.Windows.GenericWrite | PinnedIo.Windows.Delete, 0, PinnedIo.Windows.CreateNewDisposition);
                createdNew = !handle.IsInvalid;
            }

            if (handle.IsInvalid)
            {
                return Result<PinnedFile, string>.Failure(WorkspaceTools.GenericRefusalText);
            }

            var real = WorkspacePath.FinalPathOfHandle(handle);
            if (real is null || !string.Equals(real, candidate, StringComparison.OrdinalIgnoreCase))
            {
                handle.Dispose();
                return Result<PinnedFile, string>.Failure(WorkspaceTools.GenericRefusalText);
            }

            return Result<PinnedFile, string>.Success(new PinnedFile(new FileStream(handle, FileAccess.ReadWrite), real, createdNew, this, name));
        }

        if (OperatingSystem.IsLinux())
        {
            beforeOpen?.Invoke(name);
            var handle = PinnedIo.Linux.OpenAt(_handle, name, PinnedIo.Linux.ORdWr | PinnedIo.Linux.ONoFollow | PinnedIo.Linux.OCloExec);
            var createdNew = false;
            if (handle.IsInvalid)
            {
                handle = PinnedIo.Linux.OpenAt(_handle, name, PinnedIo.Linux.OCreat | PinnedIo.Linux.OExcl | PinnedIo.Linux.ONoFollow | PinnedIo.Linux.OCloExec | PinnedIo.Linux.ORdWr, 0x1B4 /* 0644 */);
                createdNew = !handle.IsInvalid;
            }

            if (handle.IsInvalid)
            {
                return Result<PinnedFile, string>.Failure(WorkspaceTools.GenericRefusalText);
            }

            return Result<PinnedFile, string>.Success(new PinnedFile(new FileStream(handle, FileAccess.ReadWrite), Path.Combine(RealPath, name), createdNew, this, name));
        }

        return Result<PinnedFile, string>.Failure(WorkspaceTools.GenericRefusalText);
    }

    /// <summary>Enumerates this directory's own entries via its open handle — never by path — so a swap between checking a name's attributes and listing it cannot substitute an outside directory's contents.</summary>
    public List<PinnedDirEntry> EnumerateEntries()
    {
        if (OperatingSystem.IsWindows())
        {
            var raw = PinnedIo.Windows.EnumerateDirectory(_handle);
            var entries = new List<PinnedDirEntry>(raw.Count);
            foreach (var (name, attributes) in raw)
            {
                const uint reparsePoint = 0x400;
                const uint directory = 0x10;
                entries.Add(new PinnedDirEntry(name, (attributes & directory) != 0, (attributes & reparsePoint) != 0));
            }

            return entries;
        }

        if (OperatingSystem.IsLinux())
        {
            return PinnedIo.Linux.EnumerateDirectory(_handle);
        }

        return [];
    }

    /// <summary>Removes this directory — only valid for one this call created, and only while it is empty — via its own pinned handle or its parent's descriptor, never a path.</summary>
    public bool TryRemoveSelf()
    {
        if (_parent is null || _name is null)
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return PinnedIo.Windows.MarkForDeletion(_handle);
        }

        if (OperatingSystem.IsLinux())
        {
            return PinnedIo.Linux.UnlinkAt(_parent._handle, _name, removeDirectory: true);
        }

        return false;
    }

    public void Dispose() => _handle.Dispose();

    private static Result<PinnedDirectory, string> VerifyWindows(SafeFileHandle handle, string expectedRealPath, bool wasCreated, PinnedDirectory? parent, string? name)
    {
        if (handle.IsInvalid)
        {
            return Result<PinnedDirectory, string>.Failure(WorkspaceTools.GenericRefusalText);
        }

        var real = WorkspacePath.FinalPathOfHandle(handle);
        if (real is null || !string.Equals(real, expectedRealPath, StringComparison.OrdinalIgnoreCase))
        {
            handle.Dispose();
            return Result<PinnedDirectory, string>.Failure(WorkspaceTools.GenericRefusalText);
        }

        return Result<PinnedDirectory, string>.Success(new PinnedDirectory(handle, real, wasCreated, parent, name));
    }

    private static Result<PinnedDirectory, string> VerifyLinuxRoot(SafeFileHandle handle, string canonicalRoot)
    {
        if (handle.IsInvalid)
        {
            return Result<PinnedDirectory, string>.Failure(WorkspaceTools.GenericRefusalText);
        }

        var real = WorkspacePath.FinalPathOfHandle(handle);
        if (real is null || !string.Equals(real, canonicalRoot, StringComparison.Ordinal))
        {
            handle.Dispose();
            return Result<PinnedDirectory, string>.Failure(WorkspaceTools.GenericRefusalText);
        }

        return Result<PinnedDirectory, string>.Success(new PinnedDirectory(handle, real, wasCreated: false, parent: null, name: null));
    }
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

    public string RealPath { get; }

    public bool CreatedNew { get; }

    /// <summary>Removes this file via its pinned parent's descriptor (Linux) or by marking its own handle for deletion (Windows) — never by path.</summary>
    public bool TryRemove()
    {
        if (OperatingSystem.IsWindows())
        {
            return PinnedIo.Windows.MarkForDeletion(Stream.SafeFileHandle);
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
