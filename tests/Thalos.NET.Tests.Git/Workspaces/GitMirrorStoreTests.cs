using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Git.Workspaces;
using Thalos.Workspaces;

namespace Thalos.Tests.Git.Workspaces;

/// <summary>A3: the mirror, bundles and commit-pinned reads, against a real local git remote.</summary>
public sealed class GitMirrorStoreTests : IDisposable
{
    private const string Author = "user.name=t";
    private const string Email = "user.email=t@example.invalid";

    private readonly string _temp = Directory.CreateTempSubdirectory("thalos-mirror-store-").FullName;

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_temp, recursive: true);
    }

    private GitMirrorStore Store(string? gitExecutable = null) =>
        new(
            new GitWorkspaceOptions { DataRoot = Path.Combine(_temp, "data"), GitExecutable = gitExecutable ?? "git" },
            NullLogger<GitMirrorStore>.Instance);

    private void Push(LocalGitRemote remote, string relativePath, string content)
    {
        var scratch = Path.Combine(_temp, "scratch-" + Guid.NewGuid().ToString("N"));
        LocalGitRemote.RunGit(_temp, "clone", "-q", "--branch", "main", remote.Url, scratch);
        var full = Path.Combine(scratch, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        LocalGitRemote.RunGit(scratch, "add", "-A");
        LocalGitRemote.RunGit(scratch, "-c", Author, "-c", Email, "commit", "-m", "change " + relativePath);
        LocalGitRemote.RunGit(scratch, "push", "-q", "origin", "main");
    }

    /// <summary>Red: skip the fetch in RetargetFetchAndValidateAsync when the mirror already exists, so the second resolve still sees the first commit.</summary>
    [Fact]
    public async Task Prepare_clones_once_and_fetches_on_every_call()
    {
        using var remote = LocalGitRemote.Create();
        var store = Store();

        var first = await store.PrepareAsync("repo", remote.Url, CancellationToken.None);
        first.IsSuccess.Should().BeTrue();
        (await store.ResolveBranchAsync(first.Value, "main", CancellationToken.None)).Value.Should().Be(remote.HeadOf("main"));

        Push(remote, "README.md", "# second\n");
        var second = await store.PrepareAsync("repo", remote.Url, CancellationToken.None);

        second.IsSuccess.Should().BeTrue();
        second.Value.Directory.Should().Be(first.Value.Directory);
        var resolved = await store.ResolveBranchAsync(second.Value, "main", CancellationToken.None);
        resolved.Value.Should().Be(remote.HeadOf("main"));
    }

    /// <summary>Red: bundle origin/main instead of the requested commit, so the clone's checkout of the earlier sha fails or lands on the later one.</summary>
    [Fact]
    public async Task A_bundle_clones_and_checks_out_the_exact_commit()
    {
        using var remote = LocalGitRemote.Create();
        var store = Store();
        var earlier = remote.HeadOf("main");
        Push(remote, "README.md", "# later\n");
        var mirror = (await store.PrepareAsync("repo", remote.Url, CancellationToken.None)).Value;
        var bundle = Path.Combine(_temp, "run.bundle");

        var written = await store.CreateBundleAsync(mirror, earlier, bundle, CancellationToken.None);

        written.IsSuccess.Should().BeTrue();
        var clone = Path.Combine(_temp, "d");
        LocalGitRemote.RunGit(_temp, "clone", "-q", bundle, clone);
        LocalGitRemote.RunGit(clone, "checkout", "-q", earlier);
        LocalGitRemote.RunGit(clone, "rev-parse", "HEAD").Should().Be(earlier);
        LocalGitRemote.RunGit(clone, "show", "HEAD:README.md").Should().Be("# sandbox");

        // The bundle holds exactly the requested commit: the clone's only branch ref points at it, not at the later tip.
        LocalGitRemote.RunGit(clone, "for-each-ref", "--format=%(objectname)", "refs/remotes/origin").Should().Be(earlier);
    }

    /// <summary>Red: skip the final update-ref -d in CreateBundleAsync, so the temporary ref stays in the mirror.</summary>
    [Fact]
    public async Task A_bundle_leaves_no_temporary_ref_in_the_mirror()
    {
        using var remote = LocalGitRemote.Create();
        var store = Store();
        var mirror = (await store.PrepareAsync("repo", remote.Url, CancellationToken.None)).Value;

        (await store.CreateBundleAsync(mirror, remote.HeadOf("main"), Path.Combine(_temp, "b.bundle"), CancellationToken.None)).IsSuccess.Should().BeTrue();

        LocalGitRemote.RunGit(mirror.Directory, "for-each-ref", "refs/heads/thalos-bundle").Should().BeEmpty();
    }

    /// <summary>Red: skip the IsFullSha check in CreateBundleAsync, so a ref name reaches update-ref and a bundle is written.</summary>
    [Fact]
    public async Task A_bundle_refuses_anything_but_a_full_sha()
    {
        using var remote = LocalGitRemote.Create();
        var store = Store();
        var mirror = (await store.PrepareAsync("repo", remote.Url, CancellationToken.None)).Value;
        var bundle = Path.Combine(_temp, "never.bundle");

        var written = await store.CreateBundleAsync(mirror, "refs/remotes/origin/main", bundle, CancellationToken.None);

        written.IsFailure.Should().BeTrue();
        File.Exists(bundle).Should().BeFalse();
    }

    /// <summary>Red: read at the branch tip instead of the commit, so the earlier read returns the later content.</summary>
    [Fact]
    public async Task ReadFile_reads_at_the_commit_and_returns_null_for_a_missing_file()
    {
        using var remote = LocalGitRemote.Create();
        var store = Store();
        var earlier = remote.HeadOf("main");
        Push(remote, "README.md", "# later\n");
        var mirror = (await store.PrepareAsync("repo", remote.Url, CancellationToken.None)).Value;

        var atEarlier = await store.ReadFileAsync(mirror, earlier, "README.md", CancellationToken.None);
        var atTip = await store.ReadFileAsync(mirror, remote.HeadOf("main"), "README.md", CancellationToken.None);
        var missing = await store.ReadFileAsync(mirror, earlier, "nope.txt", CancellationToken.None);

        atEarlier.Value.Should().Be("# sandbox\n");
        atTip.Value.Should().Be("# later\n");
        missing.IsSuccess.Should().BeTrue();
        missing.Value.Should().BeNull();
    }

    /// <summary>Red: drop the cat-file -t kind check in ReadFileAsync, so a directory reads as a failed blob read, not null.</summary>
    [Fact]
    public async Task ReadFile_returns_null_for_a_directory()
    {
        using var remote = LocalGitRemote.Create();
        Push(remote, "src/a.txt", "a\n");
        var store = Store();
        var mirror = (await store.PrepareAsync("repo", remote.Url, CancellationToken.None)).Value;

        var read = await store.ReadFileAsync(mirror, remote.HeadOf("main"), "src", CancellationToken.None);

        read.IsSuccess.Should().BeTrue();
        read.Value.Should().BeNull();
    }

    /// <summary>
    /// Red: drop the RepoRelativePath.Validate call in ReadFileAsync, so the path reaches git, which cannot start
    /// here, and the failure says so instead of refusing the path.
    /// </summary>
    [Theory]
    [InlineData("../x")]
    [InlineData(".git/config")]
    [InlineData("a/../../b")]
    public async Task ReadFile_refuses_dotdot_and_dot_git_paths(string path)
    {
        var store = Store(gitExecutable: Path.Combine(_temp, "no-such-git.exe"));
        var mirror = new GitMirror("repo", Path.Combine(_temp, "data", "mirrors", "repo"));

        var read = await store.ReadFileAsync(mirror, new string('a', 40), path, CancellationToken.None);

        read.IsFailure.Should().BeTrue();
        read.Error.Code.Should().Be(AgentErrorCode.Validation);
        read.Error.ToString().Should().NotContain("could not start");
    }

    /// <summary>Red: make MirrorConfigSurface.FindViolationAsync return null, so a mirror with core.fsmonitor set prepares successfully.</summary>
    [Fact]
    public async Task A_mirror_config_violation_fails_prepare()
    {
        using var remote = LocalGitRemote.Create();
        var store = Store();
        var mirror = (await store.PrepareAsync("repo", remote.Url, CancellationToken.None)).Value;
        LocalGitRemote.RunGit(mirror.Directory, "config", "core.fsmonitor", "true");

        var again = await store.PrepareAsync("repo", remote.Url, CancellationToken.None);

        again.IsFailure.Should().BeTrue();
        again.Error.Message.Should().Contain("outside the allowed surface");
    }
}
