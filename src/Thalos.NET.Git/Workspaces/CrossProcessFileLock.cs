namespace Thalos.Git.Workspaces;

/// <summary>
/// A lock held across processes: a lock file opened with <see cref="FileShare.None"/>. Windows refuses a second open of
/// the file while it is held; on Linux and macOS .NET takes an exclusive <c>flock</c> on it, which every other .NET
/// process opening it the same way respects. The OS releases the lock when the handle closes, including when the
/// holding process dies, so a crashed holder never leaves a stale lock behind. The file itself is never deleted:
/// deleting a lock file while someone waits on it would let two holders each own a different file. On Unix the lock
/// is advisory and relies on .NET's file locking: a process that sets <c>System.IO.DisableFileLocking</c>, or its
/// environment variable <c>DOTNET_SYSTEM_IO_DISABLEFILELOCKING</c>, does not take it, so callers keep their own
/// safeguards for the races it normally prevents.
/// </summary>
internal static class CrossProcessFileLock
{
    private const int SharingViolation = 32;
    private const int LockViolation = 33;

    /// <summary>
    /// <c>EWOULDBLOCK</c>: what a contended non-blocking <c>flock</c> fails with on Unix, where .NET reports the raw
    /// errno as the exception's <see cref="Exception.HResult"/>. 11 on Linux, 35 on macOS and the BSDs.
    /// </summary>
    private static readonly int UnixWouldBlock = OperatingSystem.IsLinux() ? 11 : 35;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Waits until the lock at <paramref name="path"/> is held by this caller, and returns the handle that holds it.
    /// Dispose the handle to release it.
    /// </summary>
    public static async Task<FileStream> AcquireAsync(string path, CancellationToken ct)
    {
        while (true)
        {
            if (TryAcquire(path) is { } held)
            {
                return held;
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Blocks until the lock at <paramref name="path"/> is held by this caller or <paramref name="timeout"/> elapses,
    /// for a synchronous caller such as a constructor. Throws <see cref="TimeoutException"/> on expiry.
    /// </summary>
    public static FileStream Acquire(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (TryAcquire(path) is { } held)
            {
                return held;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out after {timeout} waiting for the lock '{path}', held by another provider instance.");
            }

            Thread.Sleep(PollInterval);
        }
    }

    /// <summary>The held lock, or <see langword="null"/> when another holder has it. Any other failure propagates.</summary>
    private static FileStream? TryAcquire(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (IsContended(ex))
        {
            return null;
        }
    }

    private static bool IsContended(IOException ex) =>
        OperatingSystem.IsWindows()
            ? (ex.HResult & 0xFFFF) is SharingViolation or LockViolation
            : ex.HResult == UnixWouldBlock;
}
