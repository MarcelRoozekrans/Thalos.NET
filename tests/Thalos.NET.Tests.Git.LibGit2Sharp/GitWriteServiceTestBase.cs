using LibGit2Sharp;
using Thalos.Git.LibGit2Sharp;
using Xunit;
using AwesomeAssertions;
using Commands = LibGit2Sharp.Commands;

namespace Thalos.Tests.Git.LibGit2Sharp;

/// <summary>
/// Base fixture for <see cref="LibGit2SharpGitWriteService"/> tests: a real, initialized git repository in a temp
/// directory with one commit (so HEAD is born), and a real local bare repository wired as its "origin" remote, so
/// push tests exercise an actual network transport (the local file transport) rather than a fake.
/// </summary>
public abstract class GitWriteServiceTestBase : IDisposable
{
    protected const string InitialAuthorName = "Test User";
    protected const string InitialAuthorEmail = "test@example.com";

    protected LibGit2SharpGitWriteService Sut { get; } = new();

    protected string RepoPath { get; }

    protected string BareRemotePath { get; }

    protected string InitialCommitSha { get; }

    /// <summary>
    /// Runs once per test assembly load (before any test), not per test instance: redirects libgit2's Global/Xdg/System
    /// config search paths to an empty directory so no test in this assembly can accidentally read (or depend on)
    /// the real machine's git identity — every test below sets what it needs at the repository-Local config level.
    /// </summary>
    static GitWriteServiceTestBase()
    {
        var emptyConfigDir = Directory.CreateTempSubdirectory("thalos-git-empty-config-").FullName;
        GlobalSettings.SetConfigSearchPaths(ConfigurationLevel.Global, emptyConfigDir);
        GlobalSettings.SetConfigSearchPaths(ConfigurationLevel.Xdg, emptyConfigDir);
        GlobalSettings.SetConfigSearchPaths(ConfigurationLevel.System, emptyConfigDir);
    }

    protected GitWriteServiceTestBase()
    {
        RepoPath = Directory.CreateTempSubdirectory("thalos-git-").FullName;
        Repository.Init(RepoPath);
        using (var repo = new Repository(RepoPath))
        {
            repo.Config.Set("user.name", InitialAuthorName);
            repo.Config.Set("user.email", InitialAuthorEmail);
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# test repo");
            File.WriteAllText(Path.Combine(RepoPath, "to-delete.txt"), "will be removed by a later test");
            Commands.Stage(repo, "*");
            var signature = new Signature(InitialAuthorName, InitialAuthorEmail, DateTimeOffset.Now);
            var commit = repo.Commit("initial commit", signature, signature);
            InitialCommitSha = commit.Sha;
        }

        BareRemotePath = Directory.CreateTempSubdirectory("thalos-git-bare-").FullName;
        Repository.Init(BareRemotePath, isBare: true);
        using (var repo = new Repository(RepoPath))
        {
            repo.Network.Remotes.Add("origin", BareRemotePath);
        }
    }

    public void Dispose()
    {
        DeleteReadOnly(RepoPath);
        DeleteReadOnly(BareRemotePath);
        GC.SuppressFinalize(this);
    }

    /// <summary>Creates and initializes a second, unrelated temp repository for tests that need a repo other than the fixture's own.</summary>
    protected static string CreateTempRepo(bool init = true, bool bare = false)
    {
        var path = Directory.CreateTempSubdirectory("thalos-git-extra-").FullName;
        if (init)
        {
            Repository.Init(path, bare);
        }

        return path;
    }

    /// <summary>Deletes a temp git directory, clearing the read-only attribute git sets under <c>.git</c> first.</summary>
    protected static void DeleteReadOnly(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(dir, true);
    }
}
