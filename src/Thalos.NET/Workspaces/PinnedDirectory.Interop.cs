using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Thalos.Workspaces;

/// <summary>
/// P/Invoke for <see cref="PinnedDirectory"/> and <see cref="PinnedFile"/>: every directory and file operation they
/// perform once a chain is open goes through a held handle or file descriptor, never a path string, per ruling
/// "stop mutating the filesystem by path inside a tree another actor could change". Mirrors
/// <c>WorkspacePath.Interop.cs</c>'s split — a <see cref="Windows"/> and a <see cref="Linux"/> nested partial class —
/// but this file's bindings are Thalos.NET-internal (not exposed from Thalos.NET.Abstractions), because they are
/// specific to confining a write, not to <see cref="WorkspacePath.Resolve"/>'s general path canonicalisation. Where
/// <see cref="WorkspacePath.FinalPathOfHandle"/> already covers a need — verifying a handle's real path — this reuses
/// it rather than re-declaring the same native call.
/// </summary>
internal static partial class PinnedIo
{
    /// <summary>
    /// The real <c>O_DIRECTORY</c> and <c>O_NOFOLLOW</c> values for <paramref name="architecture"/>, or
    /// <see langword="null"/> for one this table does not cover. The generic Linux ABI (include/uapi/asm-generic
    /// /fcntl.h) defines <c>O_DIRECTORY</c> as 0x10000 and <c>O_NOFOLLOW</c> as 0x20000; x86_64 uses those generic
    /// values, as do riscv64 and loongarch64. arm64 overrides them (arch/arm64/include/uapi/asm/fcntl.h):
    /// <c>O_DIRECTORY</c> is 0x4000 and <c>O_NOFOLLOW</c> is 0x8000, and arm64 gives the generic bit patterns to
    /// <c>O_DIRECT</c> (0x10000) and <c>O_LARGEFILE</c> (0x20000) instead. Before round 3 this file hardcoded the
    /// generic values for every architecture, so on arm64 a pinned open asked for <c>O_DIRECT | O_LARGEFILE</c>:
    /// no directory-only open and no refusal of a symlink, so the tools failed open there. Only x64 and arm64 are in
    /// the table (ruling (a)); any other architecture, including the generic-ABI ones not verified here, gets
    /// <see langword="null"/>, and <see cref="PinnedDirectory.OpenRoot"/> refuses before any open.
    /// </summary>
    internal static (int ODirectory, int ONoFollow)? FlagsFor(Architecture architecture) => architecture switch
    {
        Architecture.X64 => (0x10000, 0x20000),
        Architecture.Arm64 => (0x4000, 0x8000),
        _ => null,
    };

    [SupportedOSPlatform("windows")]
    internal static partial class Windows
    {
        public const uint FileShareRead = 0x1;
        public const uint FileShareWrite = 0x2;
        public const uint FileShareDelete = 0x4;
        public const uint GenericRead = 0x8000_0000;
        public const uint GenericWrite = 0x4000_0000;
        public const uint Delete = 0x0001_0000;
        public const uint OpenExisting = 3;
        public const uint CreateNewDisposition = 1;
        public const uint FileFlagBackupSemantics = 0x0200_0000;
        public const uint ErrorFileNotFound = 2;
        public const uint ErrorPathNotFound = 3;
        public const uint ErrorAlreadyExists = 183;
        public const uint ErrorFileExists = 80;
        public const uint ErrorDirNotEmpty = 145;
        public const int ErrorSharingViolation = 32;
        public const int ErrorLockViolation = 33;

        private const int FileBasicInfoClass = 0;
        private const int FileDispositionInfoClass = 4;
        private const int FileFullDirectoryInfoClass = 14;
        private const int FileFullDirectoryRestartInfoClass = 15;
        private const int FileDispositionInfoExClass = 21;
        private const uint FileDispositionFlagDelete = 0x1;
        private const uint FileDispositionFlagPosixSemantics = 0x2;
        private const uint FileAttributeDirectory = 0x10;

        /// <summary>Whether <paramref name="win32Error"/> is one a caller should treat as transient contention — another handle, ours or someone else's, currently holds the object — rather than a policy refusal. Ruling (j): classify by the actual error, never guess.</summary>
        public static bool IsContention(int win32Error) => win32Error is ErrorSharingViolation or ErrorLockViolation;

        /// <summary>Whether <paramref name="handle"/> refers to a directory, via <c>FILE_BASIC_INFO.FileAttributes</c> — never a path re-check. Used to refuse opening a directory where <c>write_file</c> or <c>edit_file</c> expects a plain file (ruling (f)), instead of handing a directory handle to <see cref="FileStream"/>, which throws <see cref="ArgumentOutOfRangeException"/> from <c>SetLength</c>.</summary>
        public static bool IsDirectory(SafeFileHandle handle)
        {
            var buffer = Marshal.AllocHGlobal(64);
            try
            {
                if (GetFileInformationByHandleEx(handle, FileBasicInfoClass, buffer, 64) == 0)
                {
                    return false;
                }

                var attributes = unchecked((uint)Marshal.ReadInt32(buffer, 32));
                return (attributes & FileAttributeDirectory) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// Marks the open handle for POSIX-semantics deletion: the directory entry is unlinked as soon as this call
        /// succeeds, not deferred until every handle to it closes the way the legacy <see cref="MarkForDeletion"/>
        /// disposition is. Without this, a parent directory this call also created can still see the level's name
        /// while this call's own handle to it remains open, so an immediately-following removal of that parent finds
        /// it non-empty and stops — leaving every ancestor above the deepest level behind. Requires Windows 10 1709+;
        /// there is nothing older to fall back to in this codebase's supported range.
        /// </summary>
        public static bool MarkForDeletionPosix(SafeFileHandle handle)
        {
            var flags = FileDispositionFlagDelete | FileDispositionFlagPosixSemantics;
            return SetFileInformationByHandleU32(handle, FileDispositionInfoExClass, ref flags, 4) != 0;
        }

        /// <summary>Opens <paramref name="path"/> with the given rights, share mode and disposition, and backup semantics so a directory opens like a file. Never follows a call-site path built from anything but an already-pinned ancestor plus one raw name — see <see cref="PinnedDirectory"/>.</summary>
        public static SafeFileHandle CreateFileW(string path, uint desiredAccess, uint shareMode, uint creationDisposition, uint extraFlags = 0) =>
            CreateFileWNative(path, desiredAccess, shareMode, 0, creationDisposition, FileFlagBackupSemantics | extraFlags, 0);

        /// <summary>Creates a new, empty directory at <paramref name="path"/>. <c>CreateFileW</c> cannot create a directory — only <see cref="OpenExisting"/> is valid for it — so a missing level is created with this dedicated call and then opened and verified separately, exactly as the ruling describes. Returns false on failure, including when the name is already occupied (by a concurrent creator, or something for the caller's own open-and-verify step to catch).</summary>
        public static bool CreateDirectoryW(string path) => CreateDirectoryWNative(path, 0) != 0;

        [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial int CreateDirectoryWNative(string lpPathName, nint lpSecurityAttributes);

        /// <summary>Marks the open handle for deletion when its last copy closes — a directory (checked for emptiness immediately) or a file — never by re-resolving a path. Returns false, with the Win32 error available via <see cref="Marshal.GetLastPInvokeError"/>, on failure (e.g. a non-empty directory).</summary>
        public static bool MarkForDeletion(SafeFileHandle handle)
        {
            byte deleteFile = 1;
            return SetFileInformationByHandle(handle, FileDispositionInfoClass, ref deleteFile, 1) != 0;
        }

        private const int ErrorNoMoreFiles = 18;

        /// <summary>Enumerates one directory's own entries via its open handle — never by path — returning each name with its attributes from the same call, so a reparse point is known without a second, separate query. <c>Ok</c> is <see langword="false"/> only for a genuine error other than <c>ERROR_NO_MORE_FILES</c>, the ordinary end-of-listing signal; ruling (e): an enumeration error must come back as a failure the caller can act on, never a silently truncated listing.</summary>
        public static (List<(string Name, uint Attributes)> Entries, bool Ok) EnumerateDirectory(SafeFileHandle handle)
        {
            var entries = new List<(string Name, uint Attributes)>();
            var bufferSize = 65536;
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                var first = true;
                while (true)
                {
                    var ok = GetFileInformationByHandleEx(handle, first ? FileFullDirectoryRestartInfoClass : FileFullDirectoryInfoClass, buffer, (uint)bufferSize) != 0;
                    first = false;
                    if (!ok)
                    {
                        var lastError = Marshal.GetLastPInvokeError();
                        return (entries, lastError == ErrorNoMoreFiles);
                    }

                    var offset = 0;
                    while (true)
                    {
                        var entry = buffer + offset;
                        var nextEntryOffset = Marshal.ReadInt32(entry, 0);
                        var attributes = unchecked((uint)Marshal.ReadInt32(entry, 56));
                        var nameLength = Marshal.ReadInt32(entry, 60);
                        var name = Marshal.PtrToStringUni(entry + 68, nameLength / 2);
                        if (name is not "." and not "..")
                        {
                            entries.Add((name, attributes));
                        }

                        if (nextEntryOffset == 0)
                        {
                            break;
                        }

                        offset += nextEntryOffset;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial SafeFileHandle CreateFileWNative(string lpFileName, uint dwDesiredAccess, uint dwShareMode, nint lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

        [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
        private static partial int SetFileInformationByHandle(SafeFileHandle hFile, int fileInformationClass, ref byte lpFileInformation, uint dwBufferSize);

        [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
        private static partial int SetFileInformationByHandleU32(SafeFileHandle hFile, int fileInformationClass, ref uint lpFileInformation, uint dwBufferSize);

        [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        private static partial int GetFileInformationByHandleEx(SafeFileHandle hFile, int fileInformationClass, nint lpFileInformation, uint dwBufferSize);
    }

    [SupportedOSPlatform("linux")]
    internal static partial class Linux
    {
        // O_CREAT, O_EXCL, O_CLOEXEC and the O_RD* access modes have the same values on x86_64 and arm64, both of
        // which use asm-generic/fcntl.h for them, so these are plain constants. O_DIRECTORY and O_NOFOLLOW do not:
        // arm64 overrides the generic values, so they come from PinnedIo.FlagsFor, resolved once per chain by
        // PinnedDirectory.OpenRoot for the process architecture and carried by every PinnedDirectory in that chain.
        public const int OCloExec = 0x8_0000;
        public const int OCreat = 0x40;
        public const int OExcl = 0x80;
        public const int ORdOnly = 0x0;
        public const int ORdWr = 0x2;
        public const int AtRemoveDir = 0x200;
        public const int DefaultDirMode = 0x1FF; // 0777; reduced by the process umask, matching Directory.CreateDirectory

        public const int ENoEnt = 2;
        public const int EExist = 17;
        public const int EBusy = 16;
        public const int ENotDir = 20;

        /// <summary>Whether <paramref name="errno"/> is one a caller should treat as transient contention rather than a policy refusal. Ruling (j). Regular-file opens on Linux do not have Windows' mandatory sharing conflicts, so this is reachable only for the narrower set of cases <c>EBUSY</c> actually covers; kept for symmetry with <see cref="Windows.IsContention"/> and so the same classification vocabulary works on both OSes.</summary>
        public static bool IsContention(int errno) => errno == EBusy;

        /// <summary>Opens the starting directory fd for a chain — the one unavoidable path-based open, since a chain has to start somewhere; the caller verifies it against the canonical root immediately via <see cref="WorkspacePath.FinalPathOfHandle"/>. The caller passes the <c>O_DIRECTORY</c> and <c>O_NOFOLLOW</c> values <see cref="PinnedIo.FlagsFor"/> gave it for this process's architecture; it never calls this for an architecture that table does not cover.</summary>
        public static SafeFileHandle Open(string path, int flags) => new(OpenNative(path, flags, 0), ownsHandle: true);

        /// <summary>Opens or creates <paramref name="name"/> relative to <paramref name="dirFd"/> — never by a path string that walks through anything but this one already-pinned fd.</summary>
        public static SafeFileHandle OpenAt(SafeFileHandle dirFd, string name, int flags, int mode = 0) =>
            new(OpenAtNative(dirFd, name, flags, mode), ownsHandle: true);

        /// <summary>Creates a directory named <paramref name="name"/> relative to <paramref name="dirFd"/>. Returns false, with errno via <see cref="Marshal.GetLastPInvokeError"/>, on failure — including <c>EEXIST</c>, treated by the caller as "something is already there".</summary>
        public static bool MkDirAt(SafeFileHandle dirFd, string name) => MkDirAtNative(dirFd, name, DefaultDirMode) == 0;

        /// <summary>Removes <paramref name="name"/> relative to <paramref name="dirFd"/> — a file when <paramref name="removeDirectory"/> is false, an empty directory when true — never by a path string.</summary>
        public static bool UnlinkAt(SafeFileHandle dirFd, string name, bool removeDirectory) =>
            UnlinkAtNative(dirFd, name, removeDirectory ? AtRemoveDir : 0) == 0;

        [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int OpenNative(string pathname, int flags, int mode);

        [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int OpenAtNative(SafeFileHandle dirfd, string pathname, int flags, int mode);

        [LibraryImport("libc", EntryPoint = "mkdirat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int MkDirAtNative(SafeFileHandle dirfd, string pathname, int mode);

        [LibraryImport("libc", EntryPoint = "unlinkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int UnlinkAtNative(SafeFileHandle dirfd, string pathname, int flags);

        /// <summary>
        /// Enumerates <paramref name="dirFd"/>'s own entries via raw <c>getdents64</c> reads — never by path — using
        /// the kernel's own <c>struct linux_dirent64</c> layout, a stable syscall ABI, rather than glibc's opaque
        /// <c>struct dirent</c> that <c>readdir</c> returns (whose layout has changed between glibc versions).
        /// <c>d_type</c> comes from the same read as the name, so a symlink is known without a second, separately
        /// resolved query.
        /// </summary>
        /// <summary><c>Ok</c> is <see langword="false"/> only for a genuine <c>getdents64</c> error (a negative return); a zero return is the ordinary end-of-listing signal, not an error. Ruling (e): an enumeration error must come back as a failure the caller can act on, never a silently truncated listing.</summary>
        public static (List<PinnedDirEntry> Entries, bool Ok) EnumerateDirectory(SafeFileHandle dirFd)
        {
            const int dtDir = 4;
            const int dtLnk = 10;
            const int bufferSize = 65536;

            var entries = new List<PinnedDirEntry>();
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                while (true)
                {
                    var count = GetDEntries64(dirFd, buffer, (uint)bufferSize);
                    if (count < 0)
                    {
                        return (entries, false);
                    }

                    if (count == 0)
                    {
                        return (entries, true);
                    }

                    var offset = 0;
                    while (offset < count)
                    {
                        var entry = buffer + offset;
                        var reclen = Marshal.ReadInt16(entry, 16);
                        var dType = Marshal.ReadByte(entry, 18);
                        var name = Marshal.PtrToStringUTF8(entry + 19);
                        if (name is not "." and not ".." and not null)
                        {
                            entries.Add(new PinnedDirEntry(name, dType == dtDir, dType == dtLnk));
                        }

                        offset += reclen;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// Whether <paramref name="fd"/> refers to a directory, asked of the descriptor itself, never of a path:
        /// <c>getdents64</c> succeeds only on a directory and fails with <c>ENOTDIR</c> on anything else. Used to
        /// refuse a directory where <c>read_file</c> expects a plain file (ruling (f)): unlike a read-write open, a
        /// read-only <c>open</c> of a directory succeeds on Linux, and the first read then throws <c>EISDIR</c>. Any
        /// error other than <c>ENOTDIR</c> is reported as a directory too, so the caller refuses rather than guesses.
        /// Reading the entries moves the descriptor's position, which is harmless, since a descriptor this answers
        /// <see langword="true"/> for is closed and refused.
        /// </summary>
        public static bool IsDirectory(SafeFileHandle fd)
        {
            const int bufferSize = 4096;
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                return GetDEntries64(fd, buffer, bufferSize) >= 0 || Marshal.GetLastPInvokeError() != ENotDir;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [LibraryImport("libc", EntryPoint = "getdents64", SetLastError = true)]
        private static partial int GetDEntries64(SafeFileHandle fd, nint dirp, uint count);
    }
}
