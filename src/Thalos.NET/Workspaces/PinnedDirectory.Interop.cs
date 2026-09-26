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
    /// <summary>Test-only seam: overrides the architecture <see cref="FlagsFor"/> is evaluated against, so a test can pin the unknown-architecture refusal without needing to run this process under one. Always <see langword="null"/> in production. Kept here, on the platform-neutral outer class, rather than on <see cref="Linux"/>, since it and <see cref="FlagsFor"/> are pure functions of an <see cref="Architecture"/> value — safe to call, and to unit-test, on any host OS — and <see cref="Linux"/> itself carries a <c>[SupportedOSPlatform("linux")]</c> attribute that would otherwise make every call site outside a Linux-only branch a build error.</summary>
    internal static Architecture? ArchitectureOverrideForTesting { get; set; }

    /// <summary>The real <c>O_DIRECTORY</c> and <c>O_NOFOLLOW</c> values for <paramref name="architecture"/>, or <see langword="null"/> for one this table does not cover. x86_64 defines its own values (arch/x86/include/uapi/asm/fcntl.h); every other architecture Thalos.NET runs on, arm64 included, uses the "generic" ABI's values (asm-generic/fcntl.h) instead, which differ. The round-3 Critical finding: this file previously hardcoded the x86_64 values everywhere, so on arm64 these two resolved to arm64's <c>O_DIRECT</c> (0x4000) and <c>O_LARGEFILE</c> (0x8000) instead — no directory-only, no-follow-symlinks behaviour at all, so nothing on arm64 was ever refused for being a symlink; the tool failed open. Fixed with this per-architecture table (ruling (a)): known architectures resolve to their real values, and any other architecture — one this table has not been verified for — refuses before ever calling into libc, rather than guessing.</summary>
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
        // O_CREAT, O_EXCL, O_CLOEXEC and the O_RD* access modes are defined identically for every Linux kernel ABI
        // (asm-generic/fcntl.h; x86_64's own bits/fcntl.h does not override them), so these four are safe as plain
        // constants. O_DIRECTORY and O_NOFOLLOW are NOT: x86_64 defines its own values in arch/x86/include/uapi/asm
        // /fcntl.h, while every other architecture Thalos.NET supports (arm64 included) uses the asm-generic values,
        // which differ. The critical round-3 finding: this file previously hardcoded the x86_64 values everywhere,
        // so on arm64, ODirectory and ONoFollow resolved to arm64's O_DIRECT (0x4000) and O_LARGEFILE (0x8000)
        // instead — no directory-only, no-follow-symlinks behaviour at all, so nothing on arm64 was ever refused for
        // being a symlink; the tool failed open. Fixed with a per-architecture table (ruling (a)): known
        // architectures resolve to their real flag values, and any other architecture — one this table has not been
        // verified for — refuses before ever calling into libc, rather than guessing.
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

        /// <summary>Whether <paramref name="errno"/> is one a caller should treat as transient contention rather than a policy refusal. Ruling (j). Regular-file opens on Linux do not have Windows' mandatory sharing conflicts, so this is reachable only for the narrower set of cases <c>EBUSY</c> actually covers; kept for symmetry with <see cref="Windows.IsContention"/> and so the same classification vocabulary works on both OSes.</summary>
        public static bool IsContention(int errno) => errno == EBusy;

        private static Architecture CurrentArchitecture => ArchitectureOverrideForTesting ?? RuntimeInformation.ProcessArchitecture;

        /// <summary>Whether this process's architecture — or <see cref="PinnedIo.ArchitectureOverrideForTesting"/> — is one <see cref="PinnedIo.FlagsFor"/> covers. Every Linux entry point below checks this before its first <c>open</c>/<c>openat</c> call, per ruling (a): fail closed before any open, not after guessing wrong flag values.</summary>
        public static bool IsSupportedArchitecture => FlagsFor(CurrentArchitecture) is not null;

        public static int ODirectory => FlagsFor(CurrentArchitecture)?.ODirectory ?? 0;

        public static int ONoFollow => FlagsFor(CurrentArchitecture)?.ONoFollow ?? 0;

        /// <summary>Opens the starting directory fd for a chain — the one unavoidable path-based open, since a chain has to start somewhere; the caller verifies it against the canonical root immediately via <see cref="WorkspacePath.FinalPathOfHandle"/>. The caller checks <see cref="IsSupportedArchitecture"/> first; this is never called for an architecture <see cref="PinnedIo.FlagsFor"/> does not cover.</summary>
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

        [LibraryImport("libc", EntryPoint = "getdents64", SetLastError = true)]
        private static partial int GetDEntries64(SafeFileHandle fd, nint dirp, uint count);
    }
}
