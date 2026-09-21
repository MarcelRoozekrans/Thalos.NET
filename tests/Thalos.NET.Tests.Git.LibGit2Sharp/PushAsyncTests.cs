using LibGit2Sharp;
using Thalos;
using Thalos.Git;
using Xunit;
using AwesomeAssertions;

namespace Thalos.Tests.Git.LibGit2Sharp;

/// <summary>
/// Tests for <see cref="Thalos.Git.LibGit2Sharp.LibGit2SharpGitWriteService.PushAsync"/> against a real temp
/// repository pushing over the local file transport to a real bare repository (the fixture's "origin").
/// </summary>
public sealed class PushAsyncTests : GitWriteServiceTestBase
{
    // Breaks if the upstream is not actually configured after a push from a branch with none (the ref would land in
    // the remote but the local branch would still show no tracking branch, and UpstreamCreated would be wrong).
    [Fact]
    public async Task Pushes_and_creates_the_upstream_when_the_branch_has_none()
    {
        var created = await Sut.CreateBranchAsync(RepoPath, "feature/push", null, CancellationToken.None);
        created.IsSuccess.Should().BeTrue();

        var result = await Sut.PushAsync(RepoPath, "origin", null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.RemoteName.Should().Be("origin");
        result.Value.BranchName.Should().Be("feature/push");
        result.Value.UpstreamCreated.Should().BeTrue();

        using var repo = new Repository(RepoPath);
        repo.Head.TrackedBranch.Should().NotBeNull();

        using var bare = new Repository(BareRemotePath);
        bare.Branches["feature/push"].Should().NotBeNull();
        bare.Branches["feature/push"].Tip.Sha.Should().Be(InitialCommitSha);
    }

    // Breaks if a second push re-creates the upstream every time (UpstreamCreated should be false once tracking exists),
    // or if the new commit fails to reach the remote on the second push.
    [Fact]
    public async Task Pushes_to_an_existing_upstream_without_recreating_it()
    {
        await Sut.CreateBranchAsync(RepoPath, "feature/again", null, CancellationToken.None);
        var first = await Sut.PushAsync(RepoPath, "origin", null, CancellationToken.None);
        first.Value.UpstreamCreated.Should().BeTrue();

        File.WriteAllText(Path.Combine(RepoPath, "more.txt"), "more");
        var commit = await Sut.CommitAsync(RepoPath, "second push commit", new GitAuthor("A", "a@example.com"), CancellationToken.None);
        commit.Value.Created.Should().BeTrue();

        var second = await Sut.PushAsync(RepoPath, "origin", null, CancellationToken.None);

        second.IsSuccess.Should().BeTrue();
        second.Value.UpstreamCreated.Should().BeFalse();

        using var bare = new Repository(BareRemotePath);
        bare.Branches["feature/again"].Tip.Sha.Should().Be(commit.Value.Sha);
    }

    // Breaks if a push with no configured remote throws a raw exception, or silently reports success, instead of a
    // clean Result failure naming the missing remote.
    [Fact]
    public async Task Fails_when_the_default_remote_is_not_configured()
    {
        var noRemoteRepoPath = CreateTempRepo();
        try
        {
            using (var repo = new Repository(noRemoteRepoPath))
            {
                repo.Config.Set("user.name", InitialAuthorName);
                repo.Config.Set("user.email", InitialAuthorEmail);
                File.WriteAllText(Path.Combine(noRemoteRepoPath, "a.txt"), "a");
                Commands.Stage(repo, "*");
                var signature = new Signature(InitialAuthorName, InitialAuthorEmail, DateTimeOffset.Now);
                repo.Commit("c", signature, signature);
            }

            var result = await Sut.PushAsync(noRemoteRepoPath, null, null, CancellationToken.None);

            result.IsSuccess.Should().BeFalse();
            result.Error.Code.Should().Be(AgentErrorCode.GitOperationFailed);
        }
        finally
        {
            DeleteReadOnly(noRemoteRepoPath);
        }
    }

    // Breaks if the unborn-HEAD case throws a NullReferenceException instead of a clean Validation failure.
    [Fact]
    public async Task Fails_with_validation_when_HEAD_has_no_commits()
    {
        var emptyRepoPath = CreateTempRepo();
        try
        {
            var result = await Sut.PushAsync(emptyRepoPath, "origin", null, CancellationToken.None);

            result.IsSuccess.Should().BeFalse();
            result.Error.Code.Should().Be(AgentErrorCode.Validation);
        }
        finally
        {
            DeleteReadOnly(emptyRepoPath);
        }
    }

    // Breaks if the path validation is removed (would throw a raw LibGit2SharpException instead of a mapped Result).
    [Fact]
    public async Task Fails_with_GitRepositoryNotFound_when_the_path_is_not_a_git_repository()
    {
        var notARepo = Directory.CreateTempSubdirectory("thalos-git-not-a-repo-").FullName;
        try
        {
            var result = await Sut.PushAsync(notARepo, "origin", null, CancellationToken.None);

            result.IsSuccess.Should().BeFalse();
            result.Error.Code.Should().Be(AgentErrorCode.GitRepositoryNotFound);
        }
        finally
        {
            Directory.Delete(notARepo, true);
        }
    }
}
