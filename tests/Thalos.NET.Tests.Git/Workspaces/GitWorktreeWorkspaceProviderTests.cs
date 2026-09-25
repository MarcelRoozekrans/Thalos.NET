using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Thalos.Git;
using Thalos.Git.Workspaces;
using Thalos.Workspaces;

namespace Thalos.Tests.Git.Workspaces;

public sealed class GitWorktreeWorkspaceProviderTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("thalos-git-workspaces-").FullName;
    private readonly string _dataRoot;

    public GitWorktreeWorkspaceProviderTests()
    {
        _dataRoot = Path.Combine(_temp, "data");
    }

    [Fact]
    public async Task Create_adds_a_worktree_on_the_run_branch_cut_from_the_remote_default_branch()
    {
        using var remote = LocalGitRemote.Create(("AGENT.md", "Run dotnet test."));
        var provider = Provider(out var observer);
        var runId = Guid.NewGuid();

        var ws = (await provider.CreateAsync(new RunWorkspaceRequest(runId, "sandbox", remote.Url, "main", $"manufacture/{runId}", null), CancellationToken.None)).Value;

        ws.Root.Should().Be(Path.Combine(_dataRoot, "runs", runId.ToString()));
        Git(ws.Root, "rev-parse --abbrev-ref HEAD").Should().Be($"manufacture/{runId}");
        Git(ws.Root, "rev-parse HEAD").Should().Be(remote.HeadOf("main"));
        File.ReadAllText(Path.Combine(ws.Root, "AGENT.md")).Should().Be("Run dotnet test.");
        observer.Ready.Should().ContainSingle().Which.RunId.Should().Be(runId);
    }

    [Fact]
    public async Task A_second_provider_instance_finds_the_workspace_after_a_restart()
    {
        using var remote = LocalGitRemote.Create();
        var runId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);
        await Provider(out _, new FakeTimeProvider(createdAt)).CreateAsync(Request(remote, runId), CancellationToken.None);

        var found = await Provider(out _).FindAsync(runId, CancellationToken.None);

        found.Should().NotBeNull("a second provider instance shares no in-memory state with the one that created it — only the sidecar file does");
        found!.Branch.Should().Be($"manufacture/{runId}");
        found.CreatedAt.Should().Be(createdAt, "the sweeper's orphan grace period reads this after a restart");
    }

    [Fact]
    public async Task Remove_deletes_the_worktree_and_its_branch_and_tells_observers_first()
    {
        using var remote = LocalGitRemote.Create();
        var provider = Provider(out var observer);
        var ws = (await provider.CreateAsync(Request(remote, Guid.NewGuid()), CancellationToken.None)).Value;

        (await provider.RemoveAsync(ws.RunId, CancellationToken.None)).IsSuccess.Should().BeTrue();

        Directory.Exists(ws.Root).Should().BeFalse();
        Git(MirrorOf("sandbox"), "branch --list " + ws.Branch).Should().BeEmpty();
        observer.Removing.Should().ContainSingle();
        observer.RootExistedWhenRemoving.Should().ContainSingle().Which.Should().BeTrue("the observer must see the workspace before it is torn down, not after");
        observer.SidecarExistedWhenRemoving.Should().ContainSingle().Which.Should().BeTrue("the observer must see the workspace before it is torn down, not after");
        (await provider.FindAsync(ws.RunId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task An_unreachable_remote_fails_without_leaving_a_worktree()
    {
        var runId = Guid.NewGuid();
        var result = await Provider(out _).CreateAsync(
            new RunWorkspaceRequest(runId, "sandbox", Path.Combine(_temp, "missing.git"), "main", $"manufacture/{runId}", null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        Directory.Exists(Path.Combine(_dataRoot, "runs", runId.ToString())).Should().BeFalse();
    }

    [Fact]
    public async Task A_solution_outside_the_worktree_is_refused()
    {
        using var remote = LocalGitRemote.Create();
        var runId = Guid.NewGuid();

        var result = await Provider(out _).CreateAsync(
            new RunWorkspaceRequest(runId, "sandbox", remote.Url, "main", $"manufacture/{runId}", "../x.sln"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    /// <summary>
    /// Controller ruling from A5's review: every worktree this provider creates has <c>core.symlinks=false</c> in
    /// force, so a symlink committed to the repository is checked out as a plain file, never a real link — see
    /// <see cref="GitWorktreeWorkspaceProvider"/>'s remarks. On Windows, <c>core.symlinks</c> already defaults to
    /// <see langword="false"/>, so this assertion would hold even without the fix; it is meaningful proof only on
    /// an OS where the default is <see langword="true"/> (Linux, macOS). See the task report for the container run
    /// that removed the setting and watched a real symlink appear.
    /// </summary>
    [Fact]
    public async Task A_symlink_committed_to_the_repository_is_checked_out_as_a_regular_file()
    {
        using var remote = LocalGitRemote.CreateWithSymlink("link.txt", "../outside/secret.txt");
        var runId = Guid.NewGuid();

        var ws = (await Provider(out _).CreateAsync(
            new RunWorkspaceRequest(runId, "sandbox", remote.Url, "main", $"manufacture/{runId}", null),
            CancellationToken.None)).Value;

        var linkPath = Path.Combine(ws.Root, "link.txt");
        new FileInfo(linkPath).LinkTarget.Should().BeNull("core.symlinks=false must turn a repository symlink into a plain file, never a real link");
        File.ReadAllText(linkPath).Should().Be("../outside/secret.txt");
    }

    /// <summary>Fix round 1, ruling 2: a run that already has a live workspace is never touched by a second create for the same run id.</summary>
    [Fact]
    public async Task A_second_create_for_the_same_run_is_refused_and_leaves_the_first_workspace_untouched()
    {
        using var remote = LocalGitRemote.Create(("AGENT.md", "original"));
        var runId = Guid.NewGuid();
        var provider = Provider(out _);
        var request = new RunWorkspaceRequest(runId, "sandbox", remote.Url, "main", $"manufacture/{runId}", null);
        var ws = (await provider.CreateAsync(request, CancellationToken.None)).Value;

        var second = await provider.CreateAsync(request, CancellationToken.None);

        second.IsFailure.Should().BeTrue();
        Directory.Exists(ws.Root).Should().BeTrue();
        File.ReadAllText(Path.Combine(ws.Root, "AGENT.md")).Should().Be("original");
    }

    /// <summary>Fix round 1, ruling 4: the mirror always fetches from, and sends credentials to, the request's current Remote — not a stale one from its first clone.</summary>
    [Fact]
    public async Task Changing_the_remote_makes_the_next_create_fetch_from_it()
    {
        using var remote1 = LocalGitRemote.Create(("MARK.md", "first"));
        using var remote2 = LocalGitRemote.Create(("MARK.md", "second"));
        var provider = Provider(out _);

        var runId1 = Guid.NewGuid();
        var ws1 = (await provider.CreateAsync(new RunWorkspaceRequest(runId1, "sandbox", remote1.Url, "main", $"manufacture/{runId1}", null), CancellationToken.None)).Value;
        File.ReadAllText(Path.Combine(ws1.Root, "MARK.md")).Should().Be("first");

        var runId2 = Guid.NewGuid();
        var ws2 = (await provider.CreateAsync(new RunWorkspaceRequest(runId2, "sandbox", remote2.Url, "main", $"manufacture/{runId2}", null), CancellationToken.None)).Value;
        File.ReadAllText(Path.Combine(ws2.Root, "MARK.md")).Should().Be("second", "the same mirror ('sandbox') must fetch from the newly requested remote, not the one it first cloned from");
    }

    [Fact]
    public async Task Remote_starting_with_a_dash_is_refused()
    {
        var runId = Guid.NewGuid();
        var result = await Provider(out _).CreateAsync(
            new RunWorkspaceRequest(runId, "sandbox", "-not-a-remote", "main", $"manufacture/{runId}", null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Branch_starting_with_a_dash_is_refused()
    {
        using var remote = LocalGitRemote.Create();
        var runId = Guid.NewGuid();
        var result = await Provider(out _).CreateAsync(
            new RunWorkspaceRequest(runId, "sandbox", remote.Url, "main", "-not-a-branch", null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    /// <summary>
    /// Fix round 2, ruling 4/6: the isolation files are recreated empty by the constructor, never assumed already
    /// empty — proven by pre-seeding exactly the deterministic, <c>DataRoot</c>-relative path
    /// <see cref="GitCli"/> will use for <c>GIT_CONFIG_GLOBAL</c> with a malicious <c>core.hooksPath</c> before
    /// any provider for this <c>DataRoot</c> exists, then constructing one. This replaces fix round 1's version of
    /// this test, which mutated the process's <c>HOME</c>/<c>USERPROFILE</c> — a parallel-run hazard (ruling 6) —
    /// and is no longer even meaningful now that <see cref="GitCli"/> itself overrides <c>HOME</c> for every child
    /// process (ruling 4), which would have made that mutation inert.
    /// </summary>
    [Fact]
    public async Task A_preexisting_global_config_hook_is_wiped_at_construction_and_never_runs()
    {
        using var remote = LocalGitRemote.Create();
        var isolationDir = Path.Combine(_dataRoot, ".git-isolation");
        var hooksDir = Path.Combine(isolationDir, "hooks");
        var globalConfigPath = Path.Combine(isolationDir, "global.config");
        var marker = Path.Combine(_temp, "hook-ran.marker");

        Directory.CreateDirectory(hooksDir);
        WriteHookScript(hooksDir, marker);
        Directory.CreateDirectory(isolationDir);
        File.WriteAllText(globalConfigPath, $"[core]\n\thooksPath = {hooksDir.Replace('\\', '/')}\n");

        var runId = Guid.NewGuid();
        var result = await Provider(out _).CreateAsync(
            new RunWorkspaceRequest(runId, "sandbox", remote.Url, "main", $"manufacture/{runId}", null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        File.Exists(marker).Should().BeFalse("the constructor must recreate the isolation files empty, never assume a pre-existing one already is");
    }

    /// <summary>
    /// Fix round 2, ruling 4/6: same mechanism as the hooksPath test above, for <c>protocol.ext.allow=always</c> —
    /// pre-seeded at the exact <c>global.config</c> path before construction, wiped by the constructor.
    /// </summary>
    [Fact]
    public async Task A_preexisting_global_config_permissive_protocol_is_wiped_at_construction()
    {
        var isolationDir = Path.Combine(_dataRoot, ".git-isolation");
        var globalConfigPath = Path.Combine(isolationDir, "global.config");
        var marker = Path.Combine(_temp, "ext-ran.marker");

        Directory.CreateDirectory(isolationDir);
        File.WriteAllText(globalConfigPath, "[protocol \"ext\"]\n\tallow = always\n");

        var runId = Guid.NewGuid();
        // No quoting: a marker path is guaranteed not to contain spaces, so "ext::touch <path>" is git's own
        // plain (unquoted) word-split of the ext:: remote string.
        var result = await Provider(out _).CreateAsync(
            new RunWorkspaceRequest(runId, "sandbox", $"ext::touch {marker.Replace('\\', '/')}", "main", $"manufacture/{runId}", null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        File.Exists(marker).Should().BeFalse("the constructor must recreate global.config empty, never assume a pre-existing one already is");
    }

    /// <summary>
    /// Fix round 1, ruling 1 (CRITICAL); fixed in fix round 2 after review found the original vacuous. A command
    /// that times out reports a failure result — it never lets a <see cref="TimeoutException"/> escape
    /// <see cref="GitWorktreeWorkspaceProvider.CreateAsync"/> and skip its own cleanup. The fake git here answers
    /// <c>--version</c> normally and hangs only on <c>clone</c> — the round 1 fake hung on every call including
    /// <c>--version</c>, so only the version check ever timed out and the clone path this test means to exercise
    /// never ran at all; mutating <see cref="GitWorktreeWorkspaceProvider.CloneMirrorAsync"/> to clone straight
    /// into the final path (instead of a temp directory moved into place) left that version green.
    /// </summary>
    [Fact]
    public async Task A_command_timeout_during_clone_leaves_no_half_mirror_and_a_later_create_succeeds()
    {
        using var remote = LocalGitRemote.Create();
        var hangingCloneGit = WriteHangingCloneGit(_temp);
        var runId1 = Guid.NewGuid();

        var timeoutProvider = new GitWorktreeWorkspaceProvider(
            new GitWorkspaceOptions { DataRoot = _dataRoot, GitExecutable = hangingCloneGit, CommandTimeout = TimeSpan.FromMilliseconds(300) },
            [],
            NullLogger<GitWorktreeWorkspaceProvider>.Instance,
            TimeProvider.System);

        // If a timeout instead threw, this await would surface it as a test-ending exception rather than a Result.
        var result = await timeoutProvider.CreateAsync(
            new RunWorkspaceRequest(runId1, "sandbox", remote.Url, "main", $"manufacture/{runId1}", null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        Directory.Exists(MirrorOf("sandbox")).Should().BeFalse("a timed-out first clone must not leave a half-cloned mirror at its final path");
        var mirrorsDir = Path.Combine(_dataRoot, "mirrors");
        Directory.Exists(mirrorsDir).Should().BeTrue();
        Directory.EnumerateDirectories(mirrorsDir, ".tmp-*").Should().BeEmpty("the temporary clone directory must be cleaned up after a timeout too");

        // A later, normal create for the same repository is not blocked by anything the timed-out attempt left behind.
        var runId2 = Guid.NewGuid();
        var normalResult = await Provider(out _).CreateAsync(
            new RunWorkspaceRequest(runId2, "sandbox", remote.Url, "main", $"manufacture/{runId2}", null),
            CancellationToken.None);
        normalResult.IsSuccess.Should().BeTrue();
    }

    /// <summary>Fix round 1, ruling 3: GIT_CONFIG_GLOBAL needs git 2.32; an older git is refused rather than silently trusting host config.</summary>
    [Fact]
    public async Task A_git_older_than_2_32_is_refused()
    {
        using var remote = LocalGitRemote.Create();
        var fakeGit = WriteFakeGit(_temp, "git version 2.20.0");
        var runId = Guid.NewGuid();

        var provider = new GitWorktreeWorkspaceProvider(
            new GitWorkspaceOptions { DataRoot = _dataRoot, GitExecutable = fakeGit },
            [],
            NullLogger<GitWorktreeWorkspaceProvider>.Instance,
            TimeProvider.System);

        var result = await provider.CreateAsync(
            new RunWorkspaceRequest(runId, "sandbox", remote.Url, "main", $"manufacture/{runId}", null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.ToString().Should().Contain("2.32");
    }

    /// <summary>
    /// Fix round 1, ruling 5: a credential header goes through <c>GIT_CONFIG_COUNT</c>/<c>KEY_n</c>/<c>VALUE_n</c>
    /// environment variables, never <c>-c</c> on the command line — argv is world-readable on Linux
    /// (<c>/proc/&lt;pid&gt;/cmdline</c>), the environment of a child process is not. Verified with a "spy" git
    /// wrapper that logs every argv it receives, then execs the real git so the create still succeeds normally.
    /// </summary>
    [Fact]
    public async Task Credentials_never_appear_on_the_git_command_line()
    {
        using var remote = LocalGitRemote.Create();
        var captureFile = Path.Combine(_temp, "argv-capture.log");
        File.WriteAllText(captureFile, string.Empty);
        var spyGit = WriteSpyGit(_temp, captureFile);

        const string token = "super-secret-token-value";
        var provider = new GitWorktreeWorkspaceProvider(
            new GitWorkspaceOptions { DataRoot = _dataRoot, GitExecutable = spyGit },
            [],
            NullLogger<GitWorktreeWorkspaceProvider>.Instance,
            TimeProvider.System,
            new FakeCredentialSource(new GitCredentials("x-access-token", token)));

        var runId = Guid.NewGuid();
        var result = await provider.CreateAsync(
            new RunWorkspaceRequest(runId, "sandbox", remote.Url, "main", $"manufacture/{runId}", null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var captured = File.ReadAllText(captureFile);
        captured.Should().NotContain(token, "the credential value must never reach argv");
        captured.Should().NotContain(
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"x-access-token:{token}")),
            "the encoded credential header must never reach argv either");
    }

    /// <summary>
    /// Fix round 2, ruling 1 (IMPORTANT): the existence check and the creation run under the same per-repository
    /// lock, so a second create that starts concurrently with the first waits for the lock instead of racing
    /// <c>AddWorktreeAsync</c>'s own failure cleanup against the first call's still-live workspace. Before this
    /// fix, review found 5 of 5 runs gave one success and one failure whose cleanup deleted the successful
    /// workspace's root and sidecar.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_creates_for_the_same_run_leave_exactly_one_live_workspace()
    {
        using var remote = LocalGitRemote.Create(("AGENT.md", "original"));
        var provider = Provider(out _);
        var runId = Guid.NewGuid();
        var request = new RunWorkspaceRequest(runId, "sandbox", remote.Url, "main", $"manufacture/{runId}", null);

        var taskA = provider.CreateAsync(request, CancellationToken.None).AsTask();
        var taskB = provider.CreateAsync(request, CancellationToken.None).AsTask();
        var results = await Task.WhenAll(taskA, taskB);

        results.Count(r => r.IsSuccess).Should().Be(1, "exactly one of two concurrent creates for the same run must succeed");
        var winner = results.Single(r => r.IsSuccess).Value;
        Directory.Exists(winner.Root).Should().BeTrue();
        File.ReadAllText(Path.Combine(winner.Root, "AGENT.md")).Should().Be("original", "the survivor's own files must be untouched by the loser's cleanup");
        (await provider.FindAsync(runId, CancellationToken.None)).Should().NotBeNull();
    }

    /// <summary>
    /// Fix round 2, ruling 2 (IMPORTANT, review probe P11): a mirror validation call that cannot reach a
    /// definitive answer — here, <c>rev-parse --is-bare-repository</c> timing out — must never be treated as a
    /// positive "invalid" signal. The create fails, but the mirror and its live worktree from an earlier,
    /// successful create both survive.
    /// </summary>
    [Fact]
    public async Task A_hanging_mirror_validation_fails_the_create_without_deleting_the_mirror_or_its_live_worktree()
    {
        using var remote = LocalGitRemote.Create(("AGENT.md", "original"));
        var normalProvider = Provider(out _);
        var runId1 = Guid.NewGuid();
        var ws1 = (await normalProvider.CreateAsync(
            new RunWorkspaceRequest(runId1, "sandbox", remote.Url, "main", $"manufacture/{runId1}", null),
            CancellationToken.None)).Value;

        var hangingRevParseGit = WriteHangingRevParseGit(_temp);
        var hangingProvider = new GitWorktreeWorkspaceProvider(
            new GitWorkspaceOptions { DataRoot = _dataRoot, GitExecutable = hangingRevParseGit, CommandTimeout = TimeSpan.FromMilliseconds(300) },
            [],
            NullLogger<GitWorktreeWorkspaceProvider>.Instance,
            TimeProvider.System);

        var runId2 = Guid.NewGuid();
        var result = await hangingProvider.CreateAsync(
            new RunWorkspaceRequest(runId2, "sandbox", remote.Url, "main", $"manufacture/{runId2}", null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        // A weaker Directory.Exists check would pass even if the mirror were deleted and only partly
        // recreated (or partly destroyed by a delete that hit a locked file mid-walk) — assert the mirror is
        // still the same, intact bare repository, still tracking ws1's branch, not just that a directory exists.
        IsIntactBareRepository(MirrorOf("sandbox")).Should().BeTrue("an indeterminate validation answer must never delete or otherwise disturb the mirror");
        Directory.Exists(ws1.Root).Should().BeTrue("the earlier live worktree must survive an indeterminate mirror validation");
        File.ReadAllText(Path.Combine(ws1.Root, "AGENT.md")).Should().Be("original");
    }

    /// <summary>
    /// Fix round 2, ruling 2 (IMPORTANT, review probe P11b): a mirror positively found invalid — here,
    /// <c>config --get remote.origin.fetch</c> exiting exactly 1, after removing that config directly — is still
    /// never deleted while its <c>worktrees/</c> directory has an entry in it. The create fails, and the mirror
    /// and the live worktree it still holds both survive.
    /// </summary>
    [Fact]
    public async Task An_invalid_mirror_with_a_live_worktree_is_never_deleted()
    {
        using var remote = LocalGitRemote.Create(("AGENT.md", "original"));
        var provider = Provider(out _);
        var runId1 = Guid.NewGuid();
        var ws1 = (await provider.CreateAsync(
            new RunWorkspaceRequest(runId1, "sandbox", remote.Url, "main", $"manufacture/{runId1}", null),
            CancellationToken.None)).Value;

        // Force a positive-invalid validation answer on a mirror that still has ws1's live worktree registered.
        LocalGitRemote.RunGit(MirrorOf("sandbox"), "config", "--unset", "remote.origin.fetch");

        var runId2 = Guid.NewGuid();
        var result = await provider.CreateAsync(
            new RunWorkspaceRequest(runId2, "sandbox", remote.Url, "main", $"manufacture/{runId2}", null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        IsIntactBareRepository(MirrorOf("sandbox")).Should().BeTrue("a mirror with a live worktree must never be deleted, even when validation finds it positively invalid");
        Directory.Exists(ws1.Root).Should().BeTrue();
        File.ReadAllText(Path.Combine(ws1.Root, "AGENT.md")).Should().Be("original");
    }

    /// <summary>
    /// Fix round 2, ruling 5: a remove that fails because the worktree's own administrative directory under the
    /// mirror is already gone converges instead of failing forever — no retry of the same <c>worktree remove</c>
    /// command would ever succeed. A second remove is idempotent, same as when nothing needed removing at all.
    /// </summary>
    [Fact]
    public async Task Remove_converges_when_the_worktree_admin_directory_is_already_gone()
    {
        using var remote = LocalGitRemote.Create();
        var provider = Provider(out _);
        var ws = (await provider.CreateAsync(Request(remote, Guid.NewGuid()), CancellationToken.None)).Value;

        var adminDir = Directory.EnumerateDirectories(Path.Combine(MirrorOf("sandbox"), "worktrees")).Single();
        Directory.Delete(adminDir, recursive: true);

        var result = await provider.RemoveAsync(ws.RunId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await provider.FindAsync(ws.RunId, CancellationToken.None)).Should().BeNull();

        var second = await provider.RemoveAsync(ws.RunId, CancellationToken.None);
        second.IsSuccess.Should().BeTrue("a second remove of an already-removed workspace is idempotent");
    }

    /// <summary>Fix round 2, ruling 6: unit tests for the extraction logic directly, now that it is internal.</summary>
    [Fact]
    public void ExtractErrorDetail_returns_the_first_fatal_or_error_line()
    {
        GitWorktreeWorkspaceProvider.ExtractErrorDetail("hint: a\nfatal: b\nerror: c").Should().Be("fatal: b");
    }

    [Fact]
    public void ExtractErrorDetail_falls_back_to_the_last_non_empty_line_when_none_matches()
    {
        GitWorktreeWorkspaceProvider.ExtractErrorDetail("x\n\ny\n").Should().Be("y");
    }

    /// <summary>Fix round 2, ruling 6: an unreadable or corrupt sidecar is skipped, not fatal to the whole sweep.</summary>
    [Fact]
    public async Task ListAsync_skips_a_corrupt_sidecar_and_returns_exactly_the_real_workspace()
    {
        using var remote = LocalGitRemote.Create();
        var provider = Provider(out _);
        var runId = Guid.NewGuid();
        var ws = (await provider.CreateAsync(
            new RunWorkspaceRequest(runId, "sandbox", remote.Url, "main", $"manufacture/{runId}", null),
            CancellationToken.None)).Value;

        var garbagePath = Path.Combine(_dataRoot, "runs", Guid.NewGuid() + ".workspace.json");
        File.WriteAllText(garbagePath, "{not json");

        var listed = await provider.ListAsync(CancellationToken.None);

        listed.Should().ContainSingle().Which.RunId.Should().Be(ws.RunId);
    }

    /// <summary>
    /// Builds a provider over <see cref="_dataRoot"/> with a recording observer, a null logger and, by default,
    /// the real <see cref="TimeProvider.System"/> — the helper's default is test convenience; the provider's own
    /// <c>clock</c> parameter is required (ruling R27).
    /// </summary>
    private GitWorktreeWorkspaceProvider Provider(out RecordingObserver observer, TimeProvider? clock = null)
    {
        observer = new RecordingObserver(runId => File.Exists(SidecarPath(runId)));
        return new GitWorktreeWorkspaceProvider(
            new GitWorkspaceOptions { DataRoot = _dataRoot },
            [observer],
            NullLogger<GitWorktreeWorkspaceProvider>.Instance,
            clock ?? TimeProvider.System);
    }

    private static RunWorkspaceRequest Request(LocalGitRemote remote, Guid runId) =>
        new(runId, "sandbox", remote.Url, "main", $"manufacture/{runId}", null);

    private string MirrorOf(string repository) => Path.Combine(_dataRoot, "mirrors", repository);

    private string SidecarPath(Guid runId) => Path.Combine(_dataRoot, "runs", runId + ".workspace.json");

    private static string Git(string workingDirectory, string args) =>
        LocalGitRemote.RunGit(workingDirectory, args.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// <see langword="true"/> when <paramref name="path"/> is still a genuine, undamaged bare repository —
    /// <c>Directory.Exists</c> alone would also be true for a directory a failed, partway-through delete left
    /// behind in a corrupted state, or for one a stray reclone repopulated; this asks git itself.
    /// </summary>
    private static bool IsIntactBareRepository(string path)
    {
        try
        {
            return string.Equals(LocalGitRemote.RunGit(path, "rev-parse", "--is-bare-repository"), "true", StringComparison.Ordinal);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Writes a cross-platform <c>post-checkout</c> hook under <paramref name="hooksDir"/> that touches <paramref name="markerPath"/> if git ever runs it.</summary>
    private static void WriteHookScript(string hooksDir, string markerPath)
    {
        var hookPath = Path.Combine(hooksDir, "post-checkout");
        File.WriteAllText(hookPath, $"#!/bin/sh\ntouch \"{markerPath}\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>
    /// Writes a git wrapper that answers <c>--version</c> normally, and on a <c>clone</c> command first creates
    /// the destination directory (the last argument — real git's own first observable action, before it starts
    /// transferring data) and then hangs forever, so a kill-on-timeout genuinely leaves behind whatever an
    /// interrupted real clone would: an existing, still-forming destination directory. Everything else proxies to
    /// the real git. For the timeout-during-clone test.
    /// </summary>
    private static string WriteHangingCloneGit(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(dir, "hanging-clone-git-" + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(path,
                "@echo off\r\n" +
                "setlocal enabledelayedexpansion\r\n" +
                "echo %* | findstr /C:\"--version\" >nul\r\n" +
                "if %errorlevel%==0 (\r\n" +
                "    echo git version 2.43.0\r\n" +
                "    exit /b 0\r\n" +
                ")\r\n" +
                "echo %* | findstr /C:\"clone\" >nul\r\n" +
                "if %errorlevel%==0 (\r\n" +
                // Delayed expansion (!LASTARG!, not %LASTARG%) is required here: a plain %LASTARG% inside this
                // same parenthesized block would expand at parse time, before the for loop below ever sets it.
                "    for %%A in (%*) do set LASTARG=%%A\r\n" +
                "    mkdir \"!LASTARG!\" 2>nul\r\n" +
                // A console-free sleep: `timeout` refuses to run without a real console, `ping` does not.
                "    ping -n 9999 127.0.0.1 >nul\r\n" +
                "    exit /b 1\r\n" +
                ")\r\n" +
                "git %*\r\n" +
                "exit /b %errorlevel%\r\n");
            return path;
        }

        var scriptPath = Path.Combine(dir, "hanging-clone-git-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "case \"$*\" in\n" +
            "  *--version*)\n" +
            "    echo \"git version 2.43.0\"\n" +
            "    exit 0\n" +
            "    ;;\n" +
            "  *clone*)\n" +
            "    eval last=\\$$#\n" +
            "    mkdir -p \"$last\"\n" +
            "    sleep 9999\n" +
            "    ;;\n" +
            "esac\n" +
            "exec git \"$@\"\n");
        File.SetUnixFileMode(scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return scriptPath;
    }

    /// <summary>
    /// Writes a git wrapper that answers <c>--version</c> normally, hangs forever on
    /// <c>rev-parse --is-bare-repository</c> specifically, and proxies everything else to the real git — for the
    /// mirror-validation-timeout test (review probe P11).
    /// </summary>
    private static string WriteHangingRevParseGit(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(dir, "hanging-revparse-git-" + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(path,
                "@echo off\r\n" +
                "echo %* | findstr /C:\"--version\" >nul\r\n" +
                "if %errorlevel%==0 (\r\n" +
                "    echo git version 2.43.0\r\n" +
                "    exit /b 0\r\n" +
                ")\r\n" +
                "echo %* | findstr /C:\"--is-bare-repository\" >nul\r\n" +
                "if %errorlevel%==0 (\r\n" +
                "    ping -n 9999 127.0.0.1 >nul\r\n" +
                "    exit /b 1\r\n" +
                ")\r\n" +
                "git %*\r\n" +
                "exit /b %errorlevel%\r\n");
            return path;
        }

        var scriptPath = Path.Combine(dir, "hanging-revparse-git-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "case \"$*\" in\n" +
            "  *--version*)\n" +
            "    echo \"git version 2.43.0\"\n" +
            "    exit 0\n" +
            "    ;;\n" +
            "  *--is-bare-repository*)\n" +
            "    sleep 9999\n" +
            "    ;;\n" +
            "esac\n" +
            "exec git \"$@\"\n");
        File.SetUnixFileMode(scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return scriptPath;
    }

    /// <summary>
    /// Writes a git wrapper that reports <paramref name="versionLine"/> for <c>--version</c> and proxies every
    /// other invocation straight to the real git — so a mutation that disables the version gate makes the create
    /// this is used for actually succeed (via the real git underneath), a clean, opposite-direction assertion
    /// mismatch, rather than crashing on a non-functional stand-in for every other git command.
    /// </summary>
    private static string WriteFakeGit(string dir, string versionLine)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(dir, "fake-git-" + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(path,
                "@echo off\r\n" +
                "echo %* | findstr /C:\"--version\" >nul\r\n" +
                "if %errorlevel%==0 (\r\n" +
                $"    echo {versionLine}\r\n" +
                "    exit /b 0\r\n" +
                ")\r\n" +
                "git %*\r\n" +
                "exit /b %errorlevel%\r\n");
            return path;
        }

        var scriptPath = Path.Combine(dir, "fake-git-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "case \"$*\" in\n" +
            "  *--version*)\n" +
            $"    echo \"{versionLine}\"\n" +
            "    exit 0\n" +
            "    ;;\n" +
            "esac\n" +
            "exec git \"$@\"\n");
        File.SetUnixFileMode(scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return scriptPath;
    }

    /// <summary>
    /// Writes a "spy" git executable that appends its full argv to <paramref name="captureFile"/> and then runs
    /// the real <c>git</c> with the same arguments, so the create this is used for still succeeds normally — only
    /// the command line is observed, nothing about git's behavior changes.
    /// </summary>
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

    public void Dispose()
    {
        if (!Directory.Exists(_temp))
        {
            return;
        }

        try
        {
            // Clears the read-only attribute git sets on files under a mirror's .git/objects — otherwise a plain
            // recursive delete throws UnauthorizedAccessException on Windows.
            foreach (var file in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a worktree's index/objects can still be briefly locked right after a test's own
            // git process exits on Windows. Leaving a stray temp directory behind is not worth failing the test.
        }
    }

    private sealed class RecordingObserver(Func<Guid, bool> sidecarExists) : IRunWorkspaceObserver
    {
        public List<RunWorkspace> Ready { get; } = [];

        public List<RunWorkspace> Removing { get; } = [];

        public List<bool> RootExistedWhenRemoving { get; } = [];

        public List<bool> SidecarExistedWhenRemoving { get; } = [];

        public ValueTask OnReadyAsync(RunWorkspace workspace, CancellationToken ct)
        {
            Ready.Add(workspace);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnRemovingAsync(RunWorkspace workspace, CancellationToken ct)
        {
            Removing.Add(workspace);
            RootExistedWhenRemoving.Add(Directory.Exists(workspace.Root));
            SidecarExistedWhenRemoving.Add(sidecarExists(workspace.RunId));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeCredentialSource(GitCredentials credentials) : IGitCredentialSource
    {
        public GitCredentials? GetCredentials(string remoteUrl) => credentials;
    }
}
