using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Thalos.Git.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Sandbox;

/// <summary>
/// Opens a file on Linux for reading only when it is a regular file, without ever blocking: <c>open(2)</c> with
/// <c>O_NONBLOCK</c>, so a FIFO swapped in under the name returns at once instead of waiting for a writer, and
/// <c>O_NOFOLLOW</c>, so a final link is refused; then <c>statx</c> on the handle itself must report a regular file. The
/// type check and the read therefore describe the same open file, with no window between them.
/// </summary>
/// <remarks>
/// <c>O_NONBLOCK</c> has no effect on reading a regular file. <c>O_NOFOLLOW</c> differs per architecture; on one this
/// type does not know, every open fails closed.
/// </remarks>
[SupportedOSPlatform("linux")]
internal static partial class UnixRegularFileOpen
{
    private const int ReadOnly = 0;
    private const int NonBlock = 0x800;
    private const int CloseOnExec = 0x80000;

    private static int? NoFollow => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 or Architecture.X86 => 0x20000,
        Architecture.Arm64 or Architecture.Arm => 0x8000,
        _ => null,
    };

    /// <summary>The open handle when <paramref name="path"/> is a regular file now; otherwise why not.</summary>
    /// <param name="path">The file.</param>
    public static Result<SafeFileHandle, string> Open(string path)
    {
        if (NoFollow is not { } noFollow)
        {
            return Result<SafeFileHandle, string>.Failure($"'{path}' cannot be opened safely on {RuntimeInformation.ProcessArchitecture}");
        }

        var fd = OpenFile(path, ReadOnly | NonBlock | noFollow | CloseOnExec);
        if (fd < 0)
        {
            return Result<SafeFileHandle, string>.Failure($"'{path}' could not be opened: errno {Marshal.GetLastPInvokeError()}");
        }

        var handle = new SafeFileHandle(fd, ownsHandle: true);
        if (UnixLinkCount.IsRegularFile(handle) != true)
        {
            handle.Dispose();
            return Result<SafeFileHandle, string>.Failure($"'{path}' is no longer a regular file");
        }

        return Result<SafeFileHandle, string>.Success(handle);
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenFile(string path, int flags);
}
