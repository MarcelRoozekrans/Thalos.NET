using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Thalos.Workspaces;

public static partial class WorkspacePath
{
    /// <summary>
    /// Kernel path canonicalisation for Linux and macOS, via libc's <c>realpath</c>.
    /// </summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static partial class Unix
    {
        /// <summary>
        /// Resolves <paramref name="path"/> to its canonical, fully-symlink-resolved form, or <see langword="null"/>
        /// on failure — a dangling link, a symlink loop (<c>ELOOP</c>), a missing entry (<c>ENOENT</c>), a permission
        /// error (<c>EACCES</c>), or any other error. <c>errno</c> is deliberately not surfaced: it tells a missing
        /// host path from an unreadable one.
        /// </summary>
        public static string? RealPath(string path)
        {
            // Passing a null resolved_path is the POSIX.1-2008 / glibc and BSD extension where realpath mallocs the
            // result buffer itself; the caller frees it, hence the paired FreeNative call below.
            var buffer = RealPathNative(path, 0);
            if (buffer == 0)
                return null;

            try
            {
                return Marshal.PtrToStringUTF8(buffer);
            }
            finally
            {
                FreeNative(buffer);
            }
        }

        [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8)]
        private static partial nint RealPathNative(string path, nint resolvedPath);

        [LibraryImport("libc", EntryPoint = "free")]
        private static partial void FreeNative(nint ptr);
    }

    /// <summary>
    /// Kernel path canonicalisation for Windows, via a handle's final path.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static unsafe partial class Windows
    {
        private const uint FileShareRead = 0x1;
        private const uint FileShareWrite = 0x2;
        private const uint FileShareDelete = 0x4;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x0200_0000;

        /// <summary>
        /// Opens a handle to <paramref name="path"/> — with backup semantics, so a directory can be opened, not
        /// just a file — and asks the kernel for that handle's final, fully resolved path via
        /// <c>GetFinalPathNameByHandleW</c>, the same call <see cref="FileSystemInfo.ResolveLinkTarget"/> uses on
        /// Windows. Unlike <see cref="FileSystemInfo.ResolveLinkTarget"/>, this is called unconditionally rather
        /// than only when the path's own final component is flagged as a reparse point, so an ancestor-level
        /// junction earlier in <paramref name="path"/> is resolved too — <c>CreateFileW</c> itself follows every
        /// reparse point it meets while opening. <paramref name="path"/> is passed in its extended-length form (see
        /// <see cref="ToExtendedLengthPath"/>), so a path beyond <c>MAX_PATH</c> opens too. Returns
        /// <see langword="null"/> on any failure to open or query, including a dangling link or a reparse-point
        /// loop. The Win32 error code is deliberately not surfaced: it tells a missing host path from an unreadable
        /// one.
        /// </summary>
        public static string? GetFinalPath(string path)
        {
            using var handle = CreateFileW(ToExtendedLengthPath(path), 0, FileShareRead | FileShareWrite | FileShareDelete, 0, OpenExisting, FileFlagBackupSemantics, 0);
            if (handle.IsInvalid)
                return null;

            return GetFinalPathOfHandle(handle);
        }

        /// <summary>
        /// Queries an already-open handle's final, fully resolved path via <c>GetFinalPathNameByHandleW</c>, without
        /// opening anything itself. <see cref="GetFinalPath"/> calls this after opening its own handle;
        /// <see cref="WorkspacePath.FinalPathOfHandle"/> calls it directly on a handle a caller already holds open, so
        /// a caller closing the check-to-use gap between <see cref="Resolve"/> and its own file open never has to
        /// reopen the path — reopening by path would just re-introduce the same gap it is trying to close.
        /// </summary>
        public static string? GetFinalPathOfHandle(SafeFileHandle handle)
        {
            var buffer = new char[4096];
            while (true)
            {
                uint length;
                fixed (char* p = buffer)
                {
                    length = GetFinalPathNameByHandleW(handle, p, (uint)buffer.Length, 0);
                }

                if (length == 0)
                    return null;

                if (length < buffer.Length)
                    return StripExtendedLengthPrefix(new string(buffer, 0, (int)length));

                if (buffer.Length >= 65536)
                    return null; // pathologically long; refuse rather than grow the buffer forever

                buffer = new char[buffer.Length * 2];
            }
        }

        /// <summary>
        /// Turns a fully qualified, normalised path into its extended-length form, which lifts the <c>MAX_PATH</c>
        /// limit on <c>CreateFileW</c>: <c>C:\x</c> becomes <c>\\?\C:\x</c>, and a UNC path <c>\\server\share\x</c>
        /// becomes <c>\\?\UNC\server\share\x</c>, not <c>\\?\</c> followed by the UNC path, which names no file. A
        /// path already in extended-length or device form is left as it is. The extended-length form turns off the
        /// Win32 layer's normalisation. That is safe here: every caller passes the output of
        /// <see cref="Path.GetFullPath(string)"/>, or a <see cref="Path.Combine(string, string)"/> of it with segments
        /// the lexical checks already vetted, so no <c>"."</c>, <c>".."</c>, forward slash, or trailing dot or space
        /// is left to normalise.
        /// </summary>
        private static string ToExtendedLengthPath(string path)
        {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
                return path;

            return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        }

        /// <summary>
        /// <c>GetFinalPathNameByHandleW</c> returns the extended-length form (<c>\\?\C:\...</c>, or
        /// <c>\\?\UNC\server\share\...</c> for a UNC path); strips that prefix so the result compares like an
        /// ordinary path built by <see cref="Path.Combine(string, string)"/>.
        /// </summary>
        private static string StripExtendedLengthPrefix(string path)
        {
            if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                return @"\\" + path[8..];

            return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        }

        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            nint lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            nint hTemplateFile);

        [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW")]
        private static partial uint GetFinalPathNameByHandleW(SafeFileHandle hFile, char* lpszFilePath, uint cchFilePath, uint dwFlags);
    }
}
