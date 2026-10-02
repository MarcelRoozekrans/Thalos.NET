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

    /// <summary>
    /// A commit the mirror does not hold makes update-ref fail and write nothing, so no clean-up delete runs, and none
    /// can fail and log a warning. Red: in CreateBundleAsync, leave refMayExist true after the failed create; the spy
    /// then records an update-ref -d of the ref that was never written.
    /// </summary>
    [Fact]
    public async Task A_failed_bundle_ref_create_runs_no_clean_up_delete()
    {
        using var remote = LocalGitRemote.Create();
        var mirror = (await Store().PrepareAsync("repo", remote.Url, CancellationToken.None)).Value;
        var captureFile = Path.Combine(_temp, "bundle-argv.log");
        File.WriteAllText(captureFile, string.Empty);
        var store = Store(gitExecutable: WriteSpyGit(_temp, captureFile));
        var bundle = Path.Combine(_temp, "never.bundle");

        var written = await store.CreateBundleAsync(mirror, new string('1', 40), bundle, CancellationToken.None);

        var calls = File.ReadAllLines(captureFile).Where(l => l.Contains("update-ref", StringComparison.Ordinal)).ToList();
        written.IsFailure.Should().BeTrue();
        written.Error.Message.Should().Contain("update-ref");
        calls.Should().ContainSingle("only the failed create, no delete").Which.Should().NotContain(" -d ");
        File.Exists(bundle).Should().BeFalse();
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
    /// here, and the failure is a git operation failure, not the Validation code the assertion requires.
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

    /// <summary>
    /// Red: make the in-process gate table an instance field again, so each store has its own gate. The file lock
    /// alone still serialises where the OS allows it, which is why this asserts the gate itself.
    /// </summary>
    [Fact]
    public void Two_stores_on_one_data_root_share_one_in_process_gate()
    {
        var first = Store();
        var second = Store();

        first.GateFor("repo").Should().BeSameAs(second.GateFor("repo"));
    }

    /// <summary>Red: drop the lock taken by PrepareAsync, so the second store does not wait for the first store's lease.</summary>
    [Fact]
    public async Task Two_stores_on_one_data_root_are_serialised()
    {
        using var remote = LocalGitRemote.Create();
        var first = Store();
        var second = Store();

        var held = await first.LockRepositoryAsync("repo", CancellationToken.None);
        var waiting = second.PrepareAsync("repo", remote.Url, CancellationToken.None);

        await Task.Delay(500);
        waiting.IsCompleted.Should().BeFalse("the second store must wait for the lease the first holds");
        held.Dispose();
        (await waiting).IsSuccess.Should().BeTrue();
    }

    /// <summary>Red: drop the check-ref-format call in ResolveBranchAsync, so revision syntax reaches rev-parse and resolves.</summary>
    [Theory]
    [InlineData("main~1")]
    [InlineData("main@{1}")]
    public async Task ResolveBranch_refuses_revision_syntax(string branch)
    {
        using var remote = LocalGitRemote.Create();
        Push(remote, "README.md", "# later" + System.Environment.NewLine);
        var store = Store();
        var mirror = (await store.PrepareAsync("repo", remote.Url, CancellationToken.None)).Value;

        var resolved = await store.ResolveBranchAsync(mirror, branch, CancellationToken.None);

        resolved.IsFailure.Should().BeTrue();
        resolved.Error.Code.Should().Be(AgentErrorCode.Validation);
    }

    /// <summary>Red: drop the cat-file -e commit check in ReadFileAsync, so a commit absent from the mirror reads as a null file.</summary>
    [Fact]
    public async Task ReadFile_fails_for_a_commit_that_is_not_in_the_mirror()
    {
        using var remote = LocalGitRemote.Create();
        var store = Store();
        var mirror = (await store.PrepareAsync("repo", remote.Url, CancellationToken.None)).Value;

        var read = await store.ReadFileAsync(mirror, new string('a', 40), "README.md", CancellationToken.None);

        read.IsFailure.Should().BeTrue();
    }

    /// <summary>Red: drop ValidateMirror in ReadFileAsync, so a GitMirror naming any directory is read.</summary>
    [Fact]
    public async Task A_mirror_that_is_not_the_stores_own_is_refused()
    {
        using var remote = LocalGitRemote.Create();
        var store = Store();
        var own = (await store.PrepareAsync("repo", remote.Url, CancellationToken.None)).Value;
        var foreign = new GitMirror("repo", remote.Url);

        var read = await store.ReadFileAsync(foreign, remote.HeadOf("main"), "README.md", CancellationToken.None);

        own.Directory.Should().NotBe(foreign.Directory);
        read.IsFailure.Should().BeTrue();
        read.Error.Code.Should().Be(AgentErrorCode.Validation);
    }

    /// <summary>Red: pass bundlePath to git as given, so a relative path lands inside the mirror.</summary>
    [Fact]
    public async Task A_relative_bundle_path_is_resolved_against_the_callers_directory()
    {
        using var remote = LocalGitRemote.Create();
        var store = Store();
        var mirror = (await store.PrepareAsync("repo", remote.Url, CancellationToken.None)).Value;
        var name = "rel-" + Guid.NewGuid().ToString("N") + ".bundle";
        var expected = Path.GetFullPath(name);
        try
        {
            (await store.CreateBundleAsync(mirror, remote.HeadOf("main"), name, CancellationToken.None)).IsSuccess.Should().BeTrue();

            File.Exists(expected).Should().BeTrue();
            File.Exists(Path.Combine(mirror.Directory, name)).Should().BeFalse();
        }
        finally
        {
            File.Delete(expected);
        }
    }

    /// <summary>Writes a "spy" git executable that appends its full argv to <paramref name="captureFile"/> and then runs the real git with the same arguments.</summary>
    private static string WriteSpyGit(string dir, string captureFile)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(dir, "spy-git-" + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(path,
                "@echo off\r\n" +
                $"echo %* >> \"{captureFile}\"\r\n" +
                "git %*\r\n" +
                "exit /b %errorlevel%\r\n");
            return path;
        }

        var scriptPath = Path.Combine(dir, "spy-git-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            $"echo \"$@\" >> \"{captureFile}\"\n" +
            "exec git \"$@\"\n");
        File.SetUnixFileMode(scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return scriptPath;
    }
}
