using LibGit2Sharp;
using Thalos;
using Xunit;
using AwesomeAssertions;

namespace Thalos.Tests.Git.LibGit2Sharp;

/// <summary>Tests for <see cref="Thalos.Git.LibGit2Sharp.LibGit2SharpGitWriteService.CreateBranchAsync"/> against a real temp repository.</summary>
public sealed class CreateBranchAsyncTests : GitWriteServiceTestBase
{
    // Breaks if Commands.Checkout is removed from the implementation (branch would be created but not made current).
    [Fact]
    public async Task Creates_branch_from_HEAD_and_checks_it_out()
    {
        var result = await Sut.CreateBranchAsync(RepoPath, "feature/one", null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.BranchName.Should().Be("feature/one");
        result.Value.Sha.Should().Be(InitialCommitSha);

        using var repo = new Repository(RepoPath);
        repo.Head.FriendlyName.Should().Be("feature/one");
    }

    // Breaks if the already-exists check is removed (a duplicate create would silently succeed or switch branches).
    [Fact]
    public async Task Fails_with_GitBranchAlreadyExists_and_leaves_HEAD_untouched_when_the_branch_already_exists()
    {
        var first = await Sut.CreateBranchAsync(RepoPath, "feature/dup", null, CancellationToken.None);
        first.IsSuccess.Should().BeTrue();

        var second = await Sut.CreateBranchAsync(RepoPath, "feature/dup", null, CancellationToken.None);

        second.IsSuccess.Should().BeFalse();
        second.Error.Code.Should().Be(AgentErrorCode.GitBranchAlreadyExists);

        using var repo = new Repository(RepoPath);
        repo.Head.FriendlyName.Should().Be("feature/dup"); // where the first call left it, unaffected by the second
    }

    // Breaks if sourceRef is ignored and the branch is always created from current HEAD instead.
    [Fact]
    public async Task Branches_from_an_explicit_source_ref_rather_than_current_HEAD()
    {
        string secondCommitSha;
        using (var repo = new Repository(RepoPath))
        {
            File.WriteAllText(Path.Combine(RepoPath, "second.txt"), "second");
            Commands.Stage(repo, "*");
            var signature = new Signature(InitialAuthorName, InitialAuthorEmail, DateTimeOffset.Now);
            secondCommitSha = repo.Commit("second commit", signature, signature).Sha;
        }

        var result = await Sut.CreateBranchAsync(RepoPath, "from-first-commit", InitialCommitSha, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Sha.Should().Be(InitialCommitSha);
        result.Value.Sha.Should().NotBe(secondCommitSha);
    }

    // Breaks if an unresolvable ref throws instead of returning a Result failure.
    [Fact]
    public async Task Fails_with_validation_when_source_ref_does_not_resolve()
    {
        var result = await Sut.CreateBranchAsync(RepoPath, "feature/bad-ref", "does-not-exist", CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(AgentErrorCode.Validation);
    }

    // Breaks if the unborn-HEAD case (repo with zero commits) throws a NullReferenceException instead of a clean Result failure.
    [Fact]
    public async Task Fails_with_validation_when_the_repository_has_no_commits_yet()
    {
        var emptyRepoPath = CreateTempRepo();
        try
        {
            var result = await Sut.CreateBranchAsync(emptyRepoPath, "feature/x", null, CancellationToken.None);

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
            var result = await Sut.CreateBranchAsync(notARepo, "feature/x", null, CancellationToken.None);

            result.IsSuccess.Should().BeFalse();
            result.Error.Code.Should().Be(AgentErrorCode.GitRepositoryNotFound);
        }
        finally
        {
            Directory.Delete(notARepo, true);
        }
    }
}
