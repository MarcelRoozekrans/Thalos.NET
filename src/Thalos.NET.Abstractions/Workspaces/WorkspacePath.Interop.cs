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
        /// on failure — a dangling link, a symlink loop (<c>ELOOP</c>), a missing entry (<c>ENOENT</c>), or any
        /// other error; the caller reads <see cref="Marshal.GetLastPInvokeError"/> for <c>errno</c>.
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

        [LibraryImport("libc", EntryPoint = "realpath", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
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
        /// reparse point it meets while opening. Returns <see langword="null"/> on any failure to open or query,
        /// including a dangling link or a reparse-point loop; <paramref name="win32Error"/> carries the Win32 error
        /// code for the caller to report — never <paramref name="path"/> itself, which the caller must not echo.
        /// </summary>
        public static string? GetFinalPath(string path, out int win32Error)
        {
            using var handle = CreateFileW(path, 0, FileShareRead | FileShareWrite | FileShareDelete, 0, OpenExisting, FileFlagBackupSemantics, 0);
            if (handle.IsInvalid)
            {
                win32Error = Marshal.GetLastPInvokeError();
                return null;
            }

            var buffer = new char[4096];
            while (true)
            {
                uint length;
                fixed (char* p = buffer)
                {
                    length = GetFinalPathNameByHandleW(handle, p, (uint)buffer.Length, 0);
                }

                if (length == 0)
                {
                    win32Error = Marshal.GetLastPInvokeError();
                    return null;
                }

                if (length < buffer.Length)
                {
                    win32Error = 0;
                    return StripExtendedLengthPrefix(new string(buffer, 0, (int)length));
                }

                if (buffer.Length >= 65536)
                {
                    win32Error = 0; // pathologically long; refuse rather than grow the buffer forever
                    return null;
                }

                buffer = new char[buffer.Length * 2];
            }
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

        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            nint lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            nint hTemplateFile);

        [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
        private static partial uint GetFinalPathNameByHandleW(SafeFileHandle hFile, char* lpszFilePath, uint cchFilePath, uint dwFlags);
    }
}
