using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Thalos.Git.Workspaces;
using Thalos.Workspaces;

namespace Thalos.Tests.Git.Workspaces;

/// <summary>
/// Fix round 5: a non-canonical DataRoot, provisional-record liveness by run lock, lock files in their own namespace,
/// and NFS's false EEXIST.
/// </summary>
public sealed partial class GitWorktreeWorkspaceProviderTests
{
    /// <summary>
    /// A DataRoot spelled with forward slashes, a doubled separator and <c>..</c> names the same directory, and must
    /// work. Before round 5 the constructor compared paths built from the raw DataRoot with normalised
    /// <see cref="FileSystemInfo.FullName"/>s, never matched its own hooks, home and global config, and threw.
    /// </summary>
    [Fact]
    public async Task A_data_root_spelled_non_canonically_constructs_and_creates()
    {
        using var remote = LocalGitRemote.Create();
        var spelled = _temp.Replace('\\', '/') + "//sub/../data";
        var runId = Guid.NewGuid();

        var provider = new GitWorktreeWorkspaceProvider(
            new GitWorkspaceOptions { DataRoot = spelled },
            [],
            NullLogger<GitWorktreeWorkspaceProvider>.Instance,
            TimeProvider.System);
        var result = await provider.CreateAsync(Request(remote, runId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue($"any spelling of the same DataRoot must work{FailureText(result.IsFailure, () => result.Error)}");
        Git(result.Value.Root, "rev-parse HEAD").Should().Be(remote.HeadOf("main"));
    }

    /// <summary>
    /// A create still running — here stuck in its clone — is never removable, however old its record: the remover's
    /// clock is a day ahead, far past any grace period. Before round 5 an old enough provisional record was removed,
    /// so the every-minute sweeper could delete a slow create's workspace.
    /// </summary>
    [Fact]
    public async Task A_slow_create_is_never_removable_however_old_its_record()
    {
        using var remote = LocalGitRemote.Create();
        var runId = Guid.NewGuid();
        var slowProvider = new GitWorktreeWorkspaceProvider(
            new GitWorkspaceOptions { DataRoot = _dataRoot, GitExecutable = WriteHangingCloneGit(_temp) },
            [],
            NullLogger<GitWorktreeWorkspaceProvider>.Instance,
            TimeProvider.System);
        var sweeper = Provider(out _, new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(1)));

        using var cts = new CancellationTokenSource();
        var create = slowProvider.CreateAsync(Request(remote, runId), cts.Token).AsTask();
        await WaitForCloneToStartAsync(create);

        var removed = await sweeper.RemoveAsync(runId, CancellationToken.None);

        removed.IsFailure.Should().BeTrue("a create that is still running must never be removed, however old its record");
        File.Exists(SidecarPath(runId)).Should().BeTrue("the refused remove leaves the running create's record alone");
        await cts.CancelAsync();
        await FluentActions.Awaiting(() => create).Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// The claimant is another process, holding the run lock the way a create does, and is killed mid-create. While it
    /// lives, its record is not removable; the moment the OS has reaped it, the record is removable at once, with no
    /// grace period. The helper holds the lock with the OS's own primitive — <c>flock</c> on Linux, an unshared open
    /// on Windows — which is exactly what <see cref="CrossProcessFileLock"/> takes, and is killed.
    /// </summary>
    [Fact]
    public async Task A_claimant_process_killed_mid_create_makes_its_record_removable_at_once()
    {
        var provider = Provider(out _);
        var runId = Guid.NewGuid();
        var request = new RunWorkspaceRequest(runId, "sandbox", "https://example.invalid/repo.git", "main", $"manufacture/{runId}", null);
        (await provider.ClaimAsync(request, Path.Combine(_dataRoot, "runs", runId.ToString()), CancellationToken.None)).IsSuccess.Should().BeTrue();
        var marker = Path.Combine(_temp, $"claimant-{runId:N}.ready");

        using var claimant = StartLockHolder(RunLockPath(runId), marker);
        try
        {
            await WaitForFileAsync(marker, claimant);

            var whileAlive = await provider.RemoveAsync(runId, CancellationToken.None);
            whileAlive.IsFailure.Should().BeTrue("a claimant process that is alive holds its run lock, so its record is not removable");
            File.Exists(SidecarPath(runId)).Should().BeTrue();
        }
        finally
        {
            claimant.Kill(entireProcessTree: true);
            await claimant.WaitForExitAsync();
        }

        var afterKill = await provider.RemoveAsync(runId, CancellationToken.None);

        afterKill.IsSuccess.Should().BeTrue($"the OS released the killed claimant's run lock, so its record is removable at once{FailureText(afterKill.IsFailure, () => afterKill.Error)}");
        File.Exists(SidecarPath(runId)).Should().BeFalse();
    }

    /// <summary>
    /// Lock files live under <c>DataRoot/locks</c>, so no repository name can collide with one. Before round 5 the
    /// lock for repository <c>x</c> was <c>mirrors/.x.lock</c> — the mirror directory of repository <c>.x.lock</c>.
    /// </summary>
    [Fact]
    public async Task Repositories_named_like_lock_files_coexist_with_the_repositories_they_mimic()
    {
        using var remote = LocalGitRemote.Create();
        var provider = Provider(out _);

        var first = await provider.CreateAsync(RequestFor(remote, "x"), CancellationToken.None);
        var mimic = await provider.CreateAsync(RequestFor(remote, ".x.lock"), CancellationToken.None);
        var again = await provider.CreateAsync(RequestFor(remote, "x"), CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        mimic.IsSuccess.Should().BeTrue("a repository named like another's lock file is an ordinary repository");
        again.IsSuccess.Should().BeTrue("the repository whose lock name was mimicked must not be blocked");
    }

    /// <summary>
    /// A retransmitted NFSv3 <c>link</c> can report EEXIST for a link its first attempt made. The claimant's temp file
    /// is linked by no one else, so a link count of 2 means the destination is its own and the publish won.
    /// </summary>
    [Fact]
    public void An_eexist_publish_is_a_win_only_when_the_source_has_two_links()
    {
        AtomicPublish.EexistStillWon(2).Should().BeTrue("a second link to a temp file only its claimant links is the destination");
        AtomicPublish.EexistStillWon(1).Should().BeFalse("one link means the destination is someone else's file");
        AtomicPublish.EexistStillWon(null).Should().BeFalse("an unreadable count is a loss, never a guessed win");
    }

    /// <summary>The failure's error for an assertion message, so a red says why without a rerun.</summary>
    private static string FailureText(bool failed, Func<AgentError> error) => failed ? $" — got {error()}" : string.Empty;

    private static RunWorkspaceRequest RequestFor(LocalGitRemote remote, string repository)
    {
        var runId = Guid.NewGuid();
        return new RunWorkspaceRequest(runId, repository, remote.Url, "main", $"manufacture/{runId}", null);
    }

    private string RunLockPath(Guid runId) => Path.Combine(_dataRoot, "locks", "runs", runId + ".lock");

    /// <summary>
    /// Starts a process that takes the lock at <paramref name="lockPath"/>, writes <paramref name="marker"/> once it
    /// holds it, and then sleeps, all in one process. Linux uses <c>flock(1)</c> on the shell's own descriptor, the
    /// same exclusive <c>flock</c> .NET takes; Windows uses PowerShell to open the file unshared, the same open .NET's
    /// lock refuses to share.
    /// </summary>
    private static Process StartLockHolder(string lockPath, string marker)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell")
            {
                ArgumentList =
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    $"$f = [IO.File]::Open('{lockPath}', 'OpenOrCreate', 'ReadWrite', 'None'); Set-Content -LiteralPath '{marker}' -Value held; Start-Sleep -Seconds 600",
                },
            }
            : new ProcessStartInfo("sh")
            {
                // One process holds the lock for its whole life: the shell opens the file as fd 9, flock(1) locks
                // that open file and exits, and exec turns the shell into sleep, keeping fd 9. Killing that one
                // process is then all it takes to release the lock.
                ArgumentList = { "-c", $"exec 9>'{lockPath}' && flock 9 && touch '{marker}' && exec sleep 600" },
            };
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the lock holder.");
    }

    private static async Task WaitForFileAsync(string path, Process holder)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!File.Exists(path))
        {
            holder.HasExited.Should().BeFalse("the lock holder must still be running while it is awaited");
            DateTime.UtcNow.Should().BeBefore(deadline, "the lock holder never took its lock");
            await Task.Delay(50);
        }
    }
}
