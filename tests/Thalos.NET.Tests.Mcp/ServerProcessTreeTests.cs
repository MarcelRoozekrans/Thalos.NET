using System.Diagnostics;
using System.Runtime.Versioning;
using AwesomeAssertions.Execution;
using Thalos.Mcp;

namespace Thalos.Tests.Mcp;

/// <summary>
/// <see cref="ServerProcessTree"/>'s guards against a recycled process id, driven through its snapshot seam: the listing
/// names a real, unrelated process as a child of the server's wrapper, as one that took a descendant's id between the
/// listing and its open would appear. Only its creation time can tell it apart.
/// </summary>
public sealed class ServerProcessTreeTests
{
    /// <summary>An id no Windows process has: they are multiples of 4.</summary>
    private const int WrapperPid = 1;

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public async Task A_listed_child_created_after_the_listing_is_not_the_servers_and_is_left_running()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The process tree is only ended on Windows.");
        var startedAt = ServerProcessTree.Now();
        var snapshotAt = NextTick(ServerProcessTree.Now());
        using var victim = StartVictim(); // took a listed descendant's id after the listing
        var closedAt = Later(ServerProcessTree.Now()); // strictly after the victim, so only the listing's time refuses it

        var (found, _) = await ServerProcessTree.EndAsync(WrapperPid, startedAt, closedAt, TimeSpan.FromSeconds(5), () => Listing(victim.Id, snapshotAt));

        using var _scope = new AssertionScope();
        found.Should().Be(0, "a process created after the listing cannot be the one the listing named");
        victim.HasExited.Should().BeFalse("an unrelated process that reused a descendant's id is not terminated");
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public async Task A_child_of_the_wrapper_created_after_its_session_closed_is_not_the_servers_and_is_left_running()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The process tree is only ended on Windows.");
        var startedAt = ServerProcessTree.Now();
        var closedAt = NextTick(ServerProcessTree.Now());
        using var victim = StartVictim(); // created after the wrapper had exited, so it cannot be the wrapper's child
        var snapshotAt = NextTick(ServerProcessTree.Now());

        var (found, _) = await ServerProcessTree.EndAsync(WrapperPid, startedAt, closedAt, TimeSpan.FromSeconds(5), () => Listing(victim.Id, snapshotAt));

        using var _scope = new AssertionScope();
        found.Should().Be(0, "the wrapper's own children were all created before its session closed");
        victim.HasExited.Should().BeFalse("a process whose parent id was recycled is not terminated");
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public async Task A_child_of_the_wrapper_created_within_every_bound_is_the_servers_and_is_ended()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The process tree is only ended on Windows.");
        var startedAt = NextTick(ServerProcessTree.Now());
        using var victim = StartVictim();
        var snapshotAt = ServerProcessTree.Now();
        var closedAt = Later(snapshotAt);

        var (found, stillRunning) = await ServerProcessTree.EndAsync(WrapperPid, startedAt, closedAt, TimeSpan.FromSeconds(15), () => Listing(victim.Id, snapshotAt));

        using var _scope = new AssertionScope();
        found.Should().Be(1, "the seam's listing is honoured, so the two tests above are refused by their bounds, not by the seam");
        stillRunning.Should().Be(0);
        victim.HasExited.Should().BeTrue("a process within every bound is the server's, and is ended");
    }

    /// <summary>A listing taken at <paramref name="takenAt"/> that names process <paramref name="victim"/> as the wrapper's only child; the wrapper itself is gone.</summary>
    [SupportedOSPlatform("windows")]
    private static ServerProcessTree.ProcessSnapshot Listing(int victim, long takenAt) =>
        new(new Dictionary<int, List<int>> { [WrapperPid] = [victim] }, [victim], takenAt);

    /// <summary>A harmless process that runs for a minute unless killed, killed by its dispose if still running.</summary>
    private static VictimProcess StartVictim()
    {
        var process = Process.Start(new ProcessStartInfo("ping", "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        return new VictimProcess(process);
    }

    /// <summary>
    /// Waits until the clock has moved past <paramref name="at"/> and returns <paramref name="at"/>, so a process started
    /// afterwards is created strictly after it: the system clock can advance in steps of several milliseconds.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static long NextTick(long at)
    {
        SpinWait.SpinUntil(() => ServerProcessTree.Now() > at + TimeSpan.TicksPerMillisecond, TimeSpan.FromSeconds(1));
        return at;
    }

    /// <summary>The clock's first reading past <paramref name="at"/>: a process created no later than <paramref name="at"/> was created strictly before it.</summary>
    [SupportedOSPlatform("windows")]
    private static long Later(long at)
    {
        var later = at;
        SpinWait.SpinUntil(() => (later = ServerProcessTree.Now()) > at, TimeSpan.FromSeconds(1));
        return later;
    }

    private sealed class VictimProcess(Process process) : IDisposable
    {
        public int Id => process.Id;

        public bool HasExited => process.HasExited;

        public void Dispose()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(15_000);
            }

            process.Dispose();
        }
    }
}
