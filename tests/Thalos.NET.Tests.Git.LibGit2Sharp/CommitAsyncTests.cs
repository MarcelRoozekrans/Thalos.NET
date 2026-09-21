using LibGit2Sharp;
using Thalos;
using Thalos.Git;
using Xunit;
using AwesomeAssertions;

namespace Thalos.Tests.Git.LibGit2Sharp;

/// <summary>Tests for <see cref="Thalos.Git.LibGit2Sharp.LibGit2SharpGitWriteService.CommitAsync"/> against a real temp repository.</summary>
public sealed class CommitAsyncTests : GitWriteServiceTestBase
{
    // Breaks if staging is dropped for any one of the three change kinds (e.g. only `git add .`-style adds, missing deletions).
    [Fact]
    public async Task Stages_new_modified_and_deleted_files_without_a_separate_staging_step()
    {
        File.WriteAllText(Path.Combine(RepoPath, "new.txt"), "new file, never staged by the caller");
        File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# modified content");
        File.Delete(Path.Combine(RepoPath, "to-delete.txt"));

        var result = await Sut.CommitAsync(RepoPath, "stage everything", new GitAuthor("Author", "author@example.com"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Created.Should().BeTrue();

        using var repo = new Repository(RepoPath);
        var tree = repo.Lookup<Commit>(result.Value.Sha).Tree;
        tree["new.txt"].Should().NotBeNull();
        tree["to-delete.txt"].Should().BeNull();
        var blob = (Blob)tree["README.md"].Target;
        blob.GetContentText().Should().Be("# modified content");
    }

    // Breaks if an empty commit is created instead of being detected as "nothing to commit".
    [Fact]
    public async Task Succeeds_without_creating_a_commit_when_there_is_nothing_to_commit()
    {
        var result = await Sut.CommitAsync(RepoPath, "should be a no-op", new GitAuthor("Author", "author@example.com"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Created.Should().BeFalse();
        result.Value.Sha.Should().Be(InitialCommitSha);
    }

    // Breaks if the author parameter is ignored and the repository's config identity is used regardless.
    [Fact]
    public async Task Uses_the_given_author_identity_when_one_is_supplied()
    {
        File.WriteAllText(Path.Combine(RepoPath, "authored.txt"), "x");

        var result = await Sut.CommitAsync(RepoPath, "authored commit", new GitAuthor("Custom Author", "custom@example.com"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        using var repo = new Repository(RepoPath);
        var commit = repo.Lookup<Commit>(result.Value.Sha);
        commit.Author.Name.Should().Be("Custom Author");
        commit.Author.Email.Should().Be("custom@example.com");
    }

    // Breaks if a null author throws instead of falling back to the repository's configured user.name/user.email.
    [Fact]
    public async Task Falls_back_to_the_repository_configured_identity_when_author_is_null()
    {
        File.WriteAllText(Path.Combine(RepoPath, "unauthored.txt"), "x");

        var result = await Sut.CommitAsync(RepoPath, "unauthored commit", null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        using var repo = new Repository(RepoPath);
        var commit = repo.Lookup<Commit>(result.Value.Sha);
        commit.Author.Name.Should().Be(InitialAuthorName);
        commit.Author.Email.Should().Be(InitialAuthorEmail);
    }

    // Breaks if the missing-identity case throws instead of returning a clean Validation failure — relies on the
    // assembly-wide config isolation in GitWriteServiceTestBase so this cannot pass or fail depending on who runs it.
    [Fact]
    public async Task Fails_with_validation_when_author_is_null_and_no_identity_is_configured()
    {
        var noConfigRepoPath = CreateTempRepo();
        try
        {
            File.WriteAllText(Path.Combine(noConfigRepoPath, "c.txt"), "c");

            var result = await Sut.CommitAsync(noConfigRepoPath, "no identity", null, CancellationToken.None);

            result.IsSuccess.Should().BeFalse();
            result.Error.Code.Should().Be(AgentErrorCode.Validation);
        }
        finally
        {
            DeleteReadOnly(noConfigRepoPath);
        }
    }

    // Breaks if the path validation is removed (would throw a raw LibGit2SharpException instead of a mapped Result).
    [Fact]
    public async Task Fails_with_GitRepositoryNotFound_when_the_path_is_not_a_git_repository()
    {
        var notARepo = Directory.CreateTempSubdirectory("thalos-git-not-a-repo-").FullName;
        try
        {
            var result = await Sut.CommitAsync(notARepo, "msg", new GitAuthor("A", "a@example.com"), CancellationToken.None);

            result.IsSuccess.Should().BeFalse();
            result.Error.Code.Should().Be(AgentErrorCode.GitRepositoryNotFound);
        }
        finally
        {
            Directory.Delete(notARepo, true);
        }
    }
}
