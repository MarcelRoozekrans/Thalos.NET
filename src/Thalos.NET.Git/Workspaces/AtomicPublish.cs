using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Thalos.Git.Workspaces;

/// <summary>
/// Publishes a complete file at a path only if nothing is there yet, atomically, across processes.
/// </summary>
/// <remarks>
/// <see cref="File.Move(string, string, bool)"/> with <c>overwrite: false</c> is not enough on its own: on Windows it
/// is <c>MoveFileEx</c> without <c>MOVEFILE_REPLACE_EXISTING</c>, which the kernel refuses atomically when the
/// destination exists, but on Linux and macOS .NET implements it as an existence check followed by
/// <c>rename(2)</c>, and <c>rename(2)</c> replaces an existing destination. Two processes can both pass the check,
/// and both renames then succeed, the second replacing the first — observed as six winners out of ten racing claims.
/// On Unix this type therefore uses <c>link(2)</c>, which the kernel refuses with <c>EEXIST</c> when the destination
/// exists, and leaves removing the source name to the caller.
/// </remarks>
internal static partial class AtomicPublish
{
    private const int UnixEExist = 17;

    /// <summary>
    /// Makes the complete file at <paramref name="source"/> appear at <paramref name="destination"/>, unless
    /// something already exists there. Returns <see langword="false"/> when it does. On Unix the file then exists
    /// under both names; the caller deletes <paramref name="source"/> either way. Any other failure throws
    /// <see cref="IOException"/>.
    /// </summary>
    public static bool TryPublishNew(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                File.Move(source, destination, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(destination) || Directory.Exists(destination))
            {
                return false;
            }
        }

        return Unix.TryLink(source, destination);
    }

    [UnsupportedOSPlatform("windows")]
    private static partial class Unix
    {
        public static bool TryLink(string source, string destination)
        {
            if (Link(source, destination) == 0)
            {
                return true;
            }

            var errno = Marshal.GetLastPInvokeError();
            if (errno == UnixEExist)
            {
                return false;
            }

            throw new IOException($"Could not link '{source}' to '{destination}': {Marshal.GetPInvokeErrorMessage(errno)}", errno);
        }

        [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int Link(string oldPath, string newPath);
    }
}
