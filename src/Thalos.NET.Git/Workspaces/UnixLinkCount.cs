using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Thalos.Git.Workspaces;

/// <summary>
/// The hard-link count of a file on Linux, through <c>statx(2)</c>. Its <c>struct statx</c> has one layout on every
/// architecture, unlike <c>struct stat</c>, so no per-architecture offsets are needed: <c>stx_nlink</c> is the
/// 32-bit field at byte offset 16. <see langword="null"/> means the count could not be read — another Unix, a libc
/// without the <c>statx</c> wrapper (glibc before 2.28, musl before 1.2.5), or a failed call — and every caller
/// treats that conservatively.
/// </summary>
[SupportedOSPlatform("linux")]
internal static unsafe partial class UnixLinkCount
{
    private const int AtFdCwd = -100;
    private const int AtEmptyPath = 0x1000;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxNlink = 0x4;
    private const int NlinkOffset = 16;

    /// <summary>
    /// <c>struct statx</c> is 256 bytes; a larger buffer costs nothing and survives a future kernel that grows it.
    /// </summary>
    private const int BufferSize = 512;

    /// <summary>The link count of the file at <paramref name="path"/>, not following a final symlink.</summary>
    public static long? OfPath(string path) => Query(AtFdCwd, path, AtSymlinkNoFollow);

    private const uint StatxType = 0x1;
    private const int ModeOffset = 28;
    private const int FileTypeMask = 0xF000;
    private const int RegularFileType = 0x8000;

    /// <summary>
    /// Whether <paramref name="path"/> is a regular file, not following a final symlink: <see langword="false"/> for a
    /// FIFO, socket, device, directory or link. <see langword="null"/> when the type could not be read, for the same
    /// reasons as the link count. <c>stx_mode</c> is the 16-bit field at byte offset 28 of <c>struct statx</c>.
    /// </summary>
    public static bool? IsRegularFile(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var buffer = stackalloc byte[BufferSize];
        try
        {
            if (Statx(AtFdCwd, path, AtSymlinkNoFollow, StatxType, buffer) != 0)
            {
                return null;
            }
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }

        var mask = *(uint*)buffer;
        return (mask & StatxType) != 0 ? (*(ushort*)(buffer + ModeOffset) & FileTypeMask) == RegularFileType : null;
    }

    /// <summary>
    /// The link count of the open file <paramref name="handle"/> refers to. Zero means its last name was unlinked
    /// while it stayed open.
    /// </summary>
    public static long? OfHandle(SafeHandle handle)
    {
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            return Query((int)handle.DangerousGetHandle(), string.Empty, AtEmptyPath);
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    private static long? Query(int directoryFd, string path, int flags)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var buffer = stackalloc byte[BufferSize];
        try
        {
            if (Statx(directoryFd, path, flags, StatxNlink, buffer) != 0)
            {
                return null;
            }
        }
        catch (EntryPointNotFoundException)
        {
            // A libc without the statx wrapper: the count is unknown, which callers treat conservatively.
            return null;
        }

        var mask = *(uint*)buffer;
        return (mask & StatxNlink) != 0 ? *(uint*)(buffer + NlinkOffset) : null;
    }

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Statx(int directoryFd, string path, int flags, uint mask, byte* buffer);
}
