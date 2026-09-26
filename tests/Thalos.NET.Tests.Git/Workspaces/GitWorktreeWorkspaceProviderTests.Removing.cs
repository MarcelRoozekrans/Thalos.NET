using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Git.Workspaces;
using Thalos.Workspaces;

namespace Thalos.Tests.Git.Workspaces;

/// <summary>
/// A9 fix round 2, ruling 6: a removal marks the record <c>Removing</c> before observers hear of it, so
/// <see cref="GitWorktreeWorkspaceProvider.FindAsync"/> never reports a workspace that is being, or failed to be,
/// removed.
/// </summary>
public sealed partial class GitWorktreeWorkspaceProviderTests
{
    [Fact]
    public async Task Find_does_not_report_a_workspace_while_observers_hear_of_its_removal()
    {
        using var remote = LocalGitRemote.Create();
        var provider = Provider(out var observer);
        observer.FindWhenRemoving = runId => provider.FindAsync(runId, CancellationToken.None);
        var ws = (await provider.CreateAsync(Request(remote, Guid.NewGuid()), CancellationToken.None)).Value;

        (await provider.RemoveAsync(ws.RunId, CancellationToken.None)).IsSuccess.Should().BeTrue();

        observer.FoundWhenRemoving.Should().ContainSingle().Which.Should().BeNull(
            "an observer that stops what it started for the run must not be able to find the workspace and start it again");
    }

    [Fact]
    public async Task A_failed_git_removal_leaves_the_record_removing_and_a_later_remove_finishes_it()
    {
        using var remote = LocalGitRemote.Create();
        var provider = Provider(out var observer);
        var ws = (await provider.CreateAsync(Request(remote, Guid.NewGuid()), CancellationToken.None)).Value;
        var failingRemove = new GitWorktreeWorkspaceProvider(
            new GitWorkspaceOptions { DataRoot = _dataRoot, GitExecutable = WriteWorktreeRemoveFailingGit(_temp) },
            [observer],
            NullLogger<GitWorktreeWorkspaceProvider>.Instance,
            TimeProvider.System);

        (await failingRemove.RemoveAsync(ws.RunId, CancellationToken.None)).IsFailure.Should().BeTrue("git refused to remove the worktree");

        (await provider.FindAsync(ws.RunId, CancellationToken.None)).Should().BeNull("a workspace whose removal started is not reported, even though the removal failed");
        (await provider.ListAsync(CancellationToken.None)).Should().ContainSingle(w => w.RunId == ws.RunId, "a sweeper must still see it to finish the removal");

        (await provider.RemoveAsync(ws.RunId, CancellationToken.None)).IsSuccess.Should().BeTrue("a later remove finishes a removing record");
        Directory.Exists(ws.Root).Should().BeFalse();
        (await provider.ListAsync(CancellationToken.None)).Should().NotContain(w => w.RunId == ws.RunId);
        observer.Removing.Should().ContainSingle("observers hear of a removal once, from the call that marked the record");
    }

    [Fact]
    public async Task An_undone_create_is_not_found_while_observers_hear_that_it_is_going()
    {
        using var remote = LocalGitRemote.Create();
        using var cts = new CancellationTokenSource();
        var observer = new CancelOnReadyObserver(cts);
        var provider = new GitWorktreeWorkspaceProvider(
            new GitWorkspaceOptions { DataRoot = _dataRoot },
            [observer],
            NullLogger<GitWorktreeWorkspaceProvider>.Instance,
            TimeProvider.System);
        observer.FindWhenRemoving = runId => provider.FindAsync(runId, CancellationToken.None);

        var create = provider.CreateAsync(Request(remote, Guid.NewGuid()), cts.Token).AsTask();

        await FluentActions.Awaiting(() => create).Should().ThrowAsync<OperationCanceledException>("the observer cancelled the create after it was ready");
        observer.FoundWhenRemoving.Should().ContainSingle().Which.Should().BeNull(
            "the undo of a create that observers heard was ready marks the record before telling them it is going");
    }

    /// <summary>Cancels the create from <see cref="OnReadyAsync"/>, so the provider undoes a create observers heard was ready.</summary>
    private sealed class CancelOnReadyObserver(CancellationTokenSource cts) : IRunWorkspaceObserver
    {
        public Func<Guid, ValueTask<RunWorkspace?>>? FindWhenRemoving { get; set; }

        public List<RunWorkspace?> FoundWhenRemoving { get; } = [];

        public async ValueTask OnReadyAsync(RunWorkspace workspace, CancellationToken ct)
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        }

        public async ValueTask OnRemovingAsync(RunWorkspace workspace, CancellationToken ct) =>
            FoundWhenRemoving.Add(await FindWhenRemoving!(workspace.RunId));
    }

    /// <summary>A git that fails <c>worktree remove</c> with an ordinary error and runs the real git for everything else.</summary>
    private static string WriteWorktreeRemoveFailingGit(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(dir, "failing-remove-git-" + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(path,
                "@echo off\r\n" +
                "echo %* | findstr /C:\"worktree remove\" >nul\r\n" +
                "if %errorlevel%==0 (\r\n" +
                "    echo fatal: simulated removal failure 1>&2\r\n" +
                "    exit /b 1\r\n" +
                ")\r\n" +
                "git %*\r\n" +
                "exit /b %errorlevel%\r\n");
            return path;
        }

        var scriptPath = Path.Combine(dir, "failing-remove-git-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "case \"$*\" in\n" +
            "  *\"worktree remove\"*)\n" +
            "    echo \"fatal: simulated removal failure\" >&2\n" +
            "    exit 1\n" +
            "    ;;\n" +
            "esac\n" +
            "exec git \"$@\"\n");
        File.SetUnixFileMode(scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return scriptPath;
    }
}
