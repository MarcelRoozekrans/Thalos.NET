using Thalos.Git.Workspaces;

namespace Thalos.Tests.Git.Workspaces;

/// <summary>
/// Regression test for task A6's Linux nameless-lock check — see <see cref="CrossProcessFileLock"/>'s own remarks.
/// On Linux a waiter can open a lock file just before its holder deletes it, then lock that now-nameless file once
/// the holder closes it, while a newcomer creates and locks a fresh file at the same path: two holders at once. The
/// check closes this by releasing, and treating as contended, a freshly taken lock on a file whose <c>statx</c>
/// link count is already 0.
/// </summary>
/// <remarks>
/// <para>
/// The controller found this load-bearing by disabling the check (<c>if (false)</c> in
/// <see cref="CrossProcessFileLock"/>) and stress-running the test below on Linux: 831 and 952 cases of two
/// holders at once across two 8-second runs, against zero with the check on. That is this test's own red — see
/// the follow-up task report for the container command and output.
/// </para>
/// <para>
/// <b>Fix round 1.</b> The first version of this test — 8 threads, a <c>Task.Yield()</c> inside the held region,
/// racing through <see cref="CrossProcessFileLock.AcquireAsync"/> — was not a reliable guard: with the check
/// removed it failed only 2 of 3 runs on Linux, one false negative. Two changes fixed it. First,
/// <see cref="ThreadCount"/> went from 8 to 64, since collision probability grows roughly with the square of the
/// thread count. Second, and more importantly, the artificial <c>Task.Yield()</c> was removed and racers now go
/// through <see cref="AcquireTightAsync"/> instead of <see cref="CrossProcessFileLock.AcquireAsync"/>: on Linux,
/// <see cref="CrossProcessFileLock.TryAcquireAsync"/> itself returns immediately on genuine contention, but
/// <see cref="CrossProcessFileLock.AcquireAsync"/> still waits a 25ms poll interval before its next attempt — that
/// wait is not part of the race, and removing it lets this test hammer the lock at native syscall speed instead of
/// at most 40 attempts per thread per second. Alone, neither change was enough; combined, with the check removed,
/// this test failed 5 of 5 runs on Linux, and with the check restored it passed 3 of 3. See the follow-up task
/// report's "Fix round 1" section for the full evidence.
/// </para>
/// </remarks>
public sealed class CrossProcessFileLockTests
{
    private const int ThreadCount = 64;
    private static readonly TimeSpan RunDuration = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Windows-only: no equivalent race exists there. A held lock file there denies delete to anyone but its
    /// holder (<see cref="CrossProcessFileLock.TryOpen"/> opens with <c>FileShare.Delete</c> only on Windows, and
    /// neither read nor write sharing), so a waiter can never open a file mid-delete the way it can on Unix, and
    /// <see cref="CrossProcessFileLock.DeleteHeld"/>'s own delete simply fails for anyone racing it. This test
    /// therefore tests nothing on Windows and is skipped visibly rather than passing vacuously.
    /// </summary>
    [SkippableFact]
    public async Task Never_two_holders_of_the_same_lock_even_when_half_the_releases_delete_the_file()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the nameless-lock race this check closes, and the check itself, only exist on Linux — see CrossProcessFileLock's remarks.");

        var dir = Directory.CreateTempSubdirectory("thalos-lock-stress-");
        try
        {
            var path = Path.Combine(dir.FullName, "run.lock");
            var holders = 0;
            var maxHolders = 0;
            var releases = 0;
            var deadline = DateTime.UtcNow + RunDuration;

            var workers = Enumerable.Range(0, ThreadCount)
                .Select(_ => Task.Run(async () =>
                {
                    while (DateTime.UtcNow < deadline)
                    {
                        var held = await AcquireTightAsync(path, CancellationToken.None);

                        var now = Interlocked.Increment(ref holders);
                        RecordMax(ref maxHolders, now);
                        Interlocked.Decrement(ref holders);

                        // Half of the releases delete the lock file, as the task calls for; the other half leave it
                        // for the next holder to reopen, exercising both release paths under the same contention.
                        if (Interlocked.Increment(ref releases) % 2 == 0)
                        {
                            CrossProcessFileLock.DeleteHeld(held, path);
                        }
                        else
                        {
                            held.Dispose();
                        }
                    }
                }))
                .ToArray();

            await Task.WhenAll(workers);

            maxHolders.Should().Be(1, "the lock must never be held by more than one caller at once, even when half its releases delete the file");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Like <see cref="CrossProcessFileLock.AcquireAsync"/>, over the same <see cref="CrossProcessFileLock.TryAcquireAsync"/>
    /// the production code uses, but without its 25ms poll delay between failed attempts. That delay is not part of
    /// the race this test stresses — it exists so a production waiter does not busy-poll a lock it expects to hold
    /// for a while — and on Linux it is the only thing standing between a failed, genuinely contended attempt and
    /// the next one, so removing it multiplies the number of open+lock attempts landing near a release, which is
    /// what makes the narrow nameless-file window in <see cref="CrossProcessFileLock.TryOpen"/> reachable often
    /// enough to be a reliable guard rather than an occasional one.
    /// </summary>
    private static async Task<FileStream> AcquireTightAsync(string path, CancellationToken ct)
    {
        while (true)
        {
            if (await CrossProcessFileLock.TryAcquireAsync(path, ct).ConfigureAwait(false) is { } held)
            {
                return held;
            }
        }
    }

    private static void RecordMax(ref int target, int value)
    {
        int current;
        do
        {
            current = target;
            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
