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

        private const int FileDispositionInfoClass = 4;
        private const int FileFullDirectoryInfoClass = 14;
        private const int FileFullDirectoryRestartInfoClass = 15;

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

        /// <summary>Enumerates one directory's own entries via its open handle — never by path — returning each name with its attributes from the same call, so a reparse point is known without a second, separate query.</summary>
        public static List<(string Name, uint Attributes)> EnumerateDirectory(SafeFileHandle handle)
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
                        break; // ERROR_NO_MORE_FILES, or nothing more this call can recover from
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

            return entries;
        }

        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial SafeFileHandle CreateFileWNative(string lpFileName, uint dwDesiredAccess, uint dwShareMode, nint lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

        [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
        private static partial int SetFileInformationByHandle(SafeFileHandle hFile, int fileInformationClass, ref byte lpFileInformation, uint dwBufferSize);

        [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        private static partial int GetFileInformationByHandleEx(SafeFileHandle hFile, int fileInformationClass, nint lpFileInformation, uint dwBufferSize);
    }

    [SupportedOSPlatform("linux")]
    internal static partial class Linux
    {
        public const int ODirectory = 0x1_0000;
        public const int ONoFollow = 0x2_0000;
        public const int OCloExec = 0x8_0000;
        public const int OCreat = 0x40;
        public const int OExcl = 0x80;
        public const int ORdOnly = 0x0;
        public const int ORdWr = 0x2;
        public const int AtRemoveDir = 0x200;
        public const int DefaultDirMode = 0x1FF; // 0777; reduced by the process umask, matching Directory.CreateDirectory

        /// <summary>Opens the starting directory fd for a chain — the one unavoidable path-based open, since a chain has to start somewhere; the caller verifies it against the canonical root immediately via <see cref="WorkspacePath.FinalPathOfHandle"/>.</summary>
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
        public static List<PinnedDirEntry> EnumerateDirectory(SafeFileHandle dirFd)
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
                    if (count <= 0)
                    {
                        break;
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

            return entries;
        }

        [LibraryImport("libc", EntryPoint = "getdents64", SetLastError = true)]
        private static partial int GetDEntries64(SafeFileHandle fd, nint dirp, uint count);
    }
}
