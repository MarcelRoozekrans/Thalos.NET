using Thalos.Workspaces;

namespace Thalos.Tests.Git.Workspaces;

/// <summary>A2: a start point to cut the run branch from, the base commit it records, and the base-file and handoff roles.</summary>
public sealed partial class GitWorktreeWorkspaceProviderTests
{
    /// <summary>Pushes a second commit to the remote's main from a scratch clone, changing README.md.</summary>
    private void PushSecondCommit(LocalGitRemote remote)
    {
        var scratch = Path.Combine(_temp, "scratch-" + Guid.NewGuid().ToString("N"));
        LocalGitRemote.RunGit(_temp, "clone", "-q", "--branch", "main", remote.Url, scratch);
        File.WriteAllText(Path.Combine(scratch, "README.md"), "# second\n");
        LocalGitRemote.RunGit(scratch, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-a", "-m", "second");
        LocalGitRemote.RunGit(scratch, "push", "-q", "origin", "main");
    }

    /// <summary>Red: ignore StartPoint in AddWorktreeAsync, so the branch is cut from origin/main and HEAD is the second commit.</summary>
    [Fact]
    public async Task A_start_point_cuts_the_run_branch_from_that_commit_not_the_default_branch()
    {
        using var remote = LocalGitRemote.Create();
        var first = remote.HeadOf("main");
        PushSecondCommit(remote);
        remote.HeadOf("main").Should().NotBe(first);

        var result = await Provider(out _).CreateAsync(Request(remote, Guid.NewGuid()) with { StartPoint = first }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var ws = result.Value;
        Git(ws.Root, "rev-parse HEAD").Should().Be(first);
        ws.BaseCommit.Should().Be(first);
        File.ReadAllText(Path.Combine(ws.Root, "README.md")).Should().Be("# sandbox\n");
    }

    /// <summary>Red: leave BaseCommit null in CreateClaimedAsync.</summary>
    [Fact]
    public async Task Without_a_start_point_the_base_commit_is_the_default_branch_head()
    {
        using var remote = LocalGitRemote.Create();

        var ws = (await Provider(out _).CreateAsync(Request(remote, Guid.NewGuid()), CancellationToken.None)).Value;

        ws.BaseCommit.Should().Be(remote.HeadOf("main"));
    }

    /// <summary>Red: drop the ^[0-9a-f]{40}$ check in Validate.</summary>
    [Theory]
    [InlineData("main")]
    [InlineData("HEAD~1")]
    public async Task A_start_point_that_is_not_a_full_sha_is_refused_before_any_git_call(string startPoint)
    {
        using var remote = LocalGitRemote.Create();
        var runId = Guid.NewGuid();

        var result = await Provider(out _).CreateAsync(Request(remote, runId) with { StartPoint = startPoint }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().Contain("StartPoint");
        File.Exists(SidecarPath(runId)).Should().BeFalse("nothing was claimed");
        Directory.Exists(MirrorOf("sandbox")).Should().BeFalse("validation runs before the mirror is touched");
    }

    /// <summary>Red: read the file from disk instead of the base commit, which returns the edited text; or treat a missing file as an error instead of null.</summary>
    [Fact]
    public async Task ReadBaseFile_returns_the_file_at_the_base_commit_and_null_when_absent()
    {
        using var remote = LocalGitRemote.Create();
        var runId = Guid.NewGuid();
        var provider = Provider(out _);
        var ws = (await provider.CreateAsync(Request(remote, runId), CancellationToken.None)).Value;
        File.WriteAllText(Path.Combine(ws.Root, "README.md"), "# edited\n");
        File.WriteAllText(Path.Combine(ws.Root, "NEW.md"), "new\n");

        var present = await provider.ReadBaseFileAsync(runId, "README.md", CancellationToken.None);
        var absent = await provider.ReadBaseFileAsync(runId, "NEW.md", CancellationToken.None);
        var escape = await provider.ReadBaseFileAsync(runId, "../x", CancellationToken.None);
        var unknownRun = await provider.ReadBaseFileAsync(Guid.NewGuid(), "README.md", CancellationToken.None);

        present.IsSuccess.Should().BeTrue();
        present.Value.Should().Be("# sandbox\n");
        absent.IsSuccess.Should().BeTrue();
        absent.Value.Should().BeNull();
        escape.IsFailure.Should().BeTrue("a path outside the workspace is refused by WorkspacePath.Resolve");
        unknownRun.IsFailure.Should().BeTrue();
    }

    /// <summary>Red: make CheckoutForPublishAsync return a different workspace, or succeed for an unknown run.</summary>
    [Fact]
    public async Task CheckoutForPublish_returns_the_runs_own_worktree()
    {
        using var remote = LocalGitRemote.Create();
        var runId = Guid.NewGuid();
        var provider = Provider(out _);
        var ws = (await provider.CreateAsync(Request(remote, runId), CancellationToken.None)).Value;

        var handoff = await provider.CheckoutForPublishAsync(runId, CancellationToken.None);
        var again = await provider.CheckoutForPublishAsync(runId, CancellationToken.None);
        var unknown = await provider.CheckoutForPublishAsync(Guid.NewGuid(), CancellationToken.None);

        handoff.Value.Root.Should().Be(ws.Root);
        again.Value.Root.Should().Be(ws.Root, "the handoff is idempotent");
        unknown.IsFailure.Should().BeTrue();
    }

    /// <summary>Red: do not persist BaseCommit, so the sidecar read back after a restart holds null.</summary>
    [Fact]
    public async Task Base_commit_survives_a_restart()
    {
        using var remote = LocalGitRemote.Create();
        var runId = Guid.NewGuid();
        var created = (await Provider(out _).CreateAsync(Request(remote, runId), CancellationToken.None)).Value;

        var found = await Provider(out _).FindAsync(runId, CancellationToken.None);

        found!.BaseCommit.Should().NotBeNullOrEmpty().And.Be(created.BaseCommit);
    }
}
