using Microsoft.Extensions.Time.Testing;
using Thalos.Git.Workspaces;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Tests.Git.Workspaces;

/// <summary>
/// A14 fix round 1, F1: <see cref="RunWorkspace.CreatedAt"/> is when the workspace became ready,
/// not when its create claimed the run, so a create that waits on its repository's mirror lock does not start the
/// sweeper's orphan grace early.
/// </summary>
public sealed partial class GitWorktreeWorkspaceProviderTests
{
    [Fact]
    public async Task CreatedAt_is_when_the_workspace_became_ready_not_when_its_run_was_claimed()
    {
        using var remote = LocalGitRemote.Create();
        var claimedAt = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(claimedAt);
        var provider = Provider(out _, clock);
        var runId = Guid.NewGuid();

        // Holding the repository's mirror lock parks the create after its claim and before its clone, the way a
        // queued create or another host's first clone would.
        Task<Result<RunWorkspace, AgentError>> create;
        using (await CrossProcessFileLock.AcquireAsync(Path.Combine(_dataRoot, "locks", "mirrors", "sandbox.lock"), CancellationToken.None))
        {
            create = provider.CreateAsync(Request(remote, runId), CancellationToken.None).AsTask();
            await WaitForAsync(() => File.Exists(SidecarPath(runId)), "the create never published its claim");
            clock.Advance(TimeSpan.FromMinutes(30));
        }

        var ws = (await create).Value;

        ws.CreatedAt.Should().Be(claimedAt + TimeSpan.FromMinutes(30), "the orphan grace starts once the workspace is ready");
        (await Provider(out _).FindAsync(runId, CancellationToken.None))!.CreatedAt.Should().Be(ws.CreatedAt, "the sidecar keeps the ready instant across a restart");
    }

    /// <summary>Waits for a condition another task makes true, failing after a generous deadline rather than hanging.</summary>
    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, because);
            await Task.Delay(10);
        }
    }
}
