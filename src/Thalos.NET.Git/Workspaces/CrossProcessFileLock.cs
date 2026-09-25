namespace Thalos.Git.Workspaces;

/// <summary>
/// A lock held across processes: an exclusively opened lock file. Windows refuses any other open of the file while it
/// is held; on Linux and macOS .NET takes an exclusive <c>flock</c> on it, which every other .NET process opening it
/// the same way respects. The OS releases the lock when the handle closes, including when the holding process dies,
/// so a crashed holder never leaves a stale lock behind. On Unix the lock is advisory and relies on .NET's file
/// locking: a process that sets <c>System.IO.DisableFileLocking</c>, or its environment variable
/// <c>DOTNET_SYSTEM_IO_DISABLEFILELOCKING</c>, does not take it, so callers keep their own safeguards for the races it
/// normally prevents.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deleting a lock file.</b> Only its holder deletes it, through <see cref="DeleteHeld"/>, while still holding it.
/// On Windows the holder's handle shares delete for exactly that; since it shares neither read nor write, every
/// other open still fails, so the lock stays exclusive. On Unix a waiter can open the file just before the holder
/// deletes it and then lock that now-nameless file once the holder closes it, while a newcomer creates and locks a
/// new file at the same path: two holders. So on Linux a freshly taken lock is checked with
/// <see cref="UnixLinkCount.OfHandle"/>, and a lock on a file with no name left is released and treated as
/// contended. On Windows no waiter keeps a handle between attempts, so the case cannot arise. On another Unix the link
/// count cannot be read, and that narrow window stays open.
/// </para>
/// </remarks>
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
    /// How long a Windows access-denied open of an existing lock file is retried as a delete in progress before it is
    /// treated as the permission failure it would then be.
    /// </summary>
    private static readonly TimeSpan DeletePendingTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Waits until the lock at <paramref name="path"/> is held by this caller, and returns the handle that holds it.
    /// Dispose the handle to release it.
    /// </summary>
    public static async Task<FileStream> AcquireAsync(string path, CancellationToken ct)
    {
        while (true)
        {
            if (await TryAcquireAsync(path, ct).ConfigureAwait(false) is { } held)
            {
                return held;
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <see cref="TryAcquire"/> for an async caller: a lock file its holder is deleting, which Windows reports as
    /// access denied, is retried for up to <see cref="DeletePendingTimeout"/> without blocking a thread.
    /// </summary>
    public static async Task<FileStream?> TryAcquireAsync(string path, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + DeletePendingTimeout;
        while (true)
        {
            var attempt = TryOpen(path, DateTime.UtcNow >= deadline);
            if (!attempt.DeletePending)
            {
                return attempt.Held;
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

    /// <summary>
    /// The held lock, or <see langword="null"/> when another holder has it — never waits for a holder. Creates the
    /// lock file's directory if needed. A lock file its holder is deleting is retried briefly, see
    /// <see cref="TryOpen"/>; any other failure propagates.
    /// </summary>
    public static FileStream? TryAcquire(string path)
    {
        var deadline = DateTime.UtcNow + DeletePendingTimeout;
        while (true)
        {
            var attempt = TryOpen(path, DateTime.UtcNow >= deadline);
            if (!attempt.DeletePending)
            {
                return attempt.Held;
            }

            Thread.Sleep(PollInterval);
        }
    }

    /// <summary>
    /// One attempt. <c>DeletePending</c> is set when Windows refused the open with access denied: the state a lock
    /// file is in between its holder's <see cref="DeleteHeld"/> and that holder closing its handle, which it does
    /// straight after. Windows denies even an attribute query on such a file, so it cannot be told apart from a
    /// permission failure by looking; it is told apart by time. Once <paramref name="finalAttempt"/>, access denied
    /// propagates, as the genuine permission failure it then is.
    /// </summary>
    private static (FileStream? Held, bool DeletePending) TryOpen(string path, bool finalAttempt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FileStream held;
        try
        {
            // FileShare.None is what makes .NET take an exclusive flock on Unix; any other share mode takes a shared
            // one. Windows needs delete sharing for DeleteHeld, and read and write stay unshared, so it is exclusive.
            held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, OperatingSystem.IsWindows() ? FileShare.Delete : FileShare.None);
        }
        catch (IOException ex) when (IsContended(ex))
        {
            return (null, false);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows() && !finalAttempt)
        {
            return (null, true);
        }

        if (OperatingSystem.IsLinux() && UnixLinkCount.OfHandle(held.SafeFileHandle) == 0)
        {
            // The holder before this one deleted the file after this open and before this lock: see the remarks.
            held.Dispose();
            return (null, false);
        }

        return (held, false);
    }

    /// <summary>
    /// Deletes the lock file at <paramref name="path"/> while <paramref name="held"/> still holds it, then releases
    /// it. For a lock that belongs to something being deleted for good, such as a removed run.
    /// </summary>
    public static void DeleteHeld(FileStream held, string path)
    {
        try
        {
            File.Delete(path);
        }
        finally
        {
            held.Dispose();
        }
    }

    private static bool IsContended(IOException ex) =>
        OperatingSystem.IsWindows()
            ? (ex.HResult & 0xFFFF) is SharingViolation or LockViolation
            : ex.HResult == UnixWouldBlock;
}
