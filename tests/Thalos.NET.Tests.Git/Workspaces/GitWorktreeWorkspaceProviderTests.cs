using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Thalos.Git;
using Thalos.Git.Workspaces;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Tests.Git.Workspaces;

public sealed partial class GitWorktreeWorkspaceProviderTests : IDisposable
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
    /// How many provider instances race at once in <see cref="P1y_two_provider_instances_racing_the_same_run_leave_exactly_one_live_workspace"/>
    /// and <see cref="P1x_the_same_run_id_against_two_repositories_leaves_exactly_one_live_workspace"/>. The claim's
    /// vulnerable window under a broken claim — between one caller's <see cref="FileStream"/> closing and another's
    /// opening — is narrow: a plain two-way race can land outside it even with the atomic claim removed, which
    /// matches review's own description of this probe as non-deterministic ("both calls failed... or one reported
    /// success"). Racing many callers at once, instead of just two, turns the narrow pairwise window into one of
    /// <c>RacerCount</c> choose 2 chances to land inside it in a single attempt.
    /// </summary>
    private const int RacerCount = 12;

    /// <summary>
    /// Fix round 3, ruling 1 (review probe P1y): ownership of a run is claimed atomically across provider
    /// instances — many providers sharing one <see cref="GitWorkspaceOptions.DataRoot"/>, as an API host and a CLI
    /// host (and more of each) would, racing <see cref="GitWorktreeWorkspaceProvider.CreateAsync"/> for the very
    /// same request. Before this fix, review found calls could fail and leave nothing, or one could report success
    /// with its root already gone, because the existence check and the claim were two separate steps a second
    /// process could interleave with. Exactly one of the <see cref="RacerCount"/> provider instances must win, and
    /// the survivor must still work as a real git checkout — not merely have a directory present.
    /// </summary>
    [Fact]
    public async Task P1y_two_provider_instances_racing_the_same_run_leave_exactly_one_live_workspace()
    {
        using var remote = LocalGitRemote.Create(("AGENT.md", "original"));
        var providers = Enumerable.Range(0, RacerCount).Select(index => Provider(out _)).ToArray();
        var runId = Guid.NewGuid();
        var request = new RunWorkspaceRequest(runId, "sandbox", remote.Url, "main", $"manufacture/{runId}", null);

        // A Barrier tightens the race window: every call reaches its claim attempt as close to simultaneously as
        // possible, rather than one starting a few instructions ahead of another just from being awaited first. If
        // any call let an exception escape instead of returning a failure Result, Task.WhenAll would rethrow it
        // here, which is itself evidence of a broken claim.
        var barrier = new Barrier(RacerCount);
        var tasks = providers.Select(p => Task.Run(async () => { barrier.SignalAndWait(); return await p.CreateAsync(request, CancellationToken.None); })).ToArray();
        var results = await Task.WhenAll(tasks);

        var (successCount, winner) = CountSuccesses(results);
        successCount.Should().Be(1, "exactly one of many provider instances racing the same run must win the claim");
        // Git throws when git status exits nonzero, so the call itself checks the survivor is a working checkout.
        _ = Git(winner!.Root, "status");
        File.ReadAllText(Path.Combine(winner.Root, "AGENT.md")).Should().Be("original", "the survivor's own files must be untouched by any loser");
        (await providers[0].FindAsync(runId, CancellationToken.None)).Should().NotBeNull();
    }

    /// <summary>
    /// Fix round 3, ruling 1 (review probe P1x): the same guarantee as <see cref="P1y_two_provider_instances_racing_the_same_run_leave_exactly_one_live_workspace"/>,
    /// but the racing calls name the same run id against two <em>different</em> repositories, <see cref="RacerCount"/>
    /// split evenly between them. Before this fix, review found this threw an uncaught <see cref="IOException"/>
    /// writing <c>workspace.json.tmp</c>, because the sidecar path is keyed on the run id alone — calls for
    /// different repositories still collide on one sidecar file. The atomic <see cref="FileMode.CreateNew"/> claim
    /// must resolve this the same way as P1y: one winner, nothing thrown, every loser's repository untouched.
    /// This test cannot turn red for a broken claim: with the claim made non-atomic, every extra winner still
    /// collides on <c>git worktree add</c> against the one root the run id implies, and git's own refusal converges
    /// to one success. <see cref="ClaimAsync_lets_exactly_one_racing_call_win_across_repositories"/> carries the
    /// claim's red; this test is positive end-to-end confirmation only.
    /// </summary>
    [Fact]
    public async Task P1x_the_same_run_id_against_two_repositories_leaves_exactly_one_live_workspace()
    {
        using var remote1 = LocalGitRemote.Create(("AGENT.md", "repo one"));
        using var remote2 = LocalGitRemote.Create(("AGENT.md", "repo two"));
        // One provider instance per racer, not one shared instance: a shared instance's per-repository lock (see
        // the class remarks) would serialise every racer for the same repository through CreateClaimedAsync one at
        // a time, masking the very race this test exists to expose — real cross-process racers, an API host and a
        // CLI host, share no such in-process lock at all.
        var providers = Enumerable.Range(0, RacerCount).Select(index => Provider(out _)).ToArray();
        var runId = Guid.NewGuid();
        var request1 = new RunWorkspaceRequest(runId, "repo-one", remote1.Url, "main", $"manufacture/{runId}", null);
        var request2 = new RunWorkspaceRequest(runId, "repo-two", remote2.Url, "main", $"manufacture/{runId}", null);

        var barrier = new Barrier(RacerCount);
        var tasks = providers
            .Select((p, index) => Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await p.CreateAsync(index % 2 == 0 ? request1 : request2, CancellationToken.None);
            }))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        var (successCount, winner) = CountSuccesses(results);
        successCount.Should().Be(1, "exactly one repository must win the claim for a shared run id");
        // Git throws when git status exits nonzero, so the call itself checks the survivor is a working checkout.
        _ = Git(winner!.Root, "status");
        (await providers[0].FindAsync(runId, CancellationToken.None)).Should().NotBeNull();
    }

    /// <summary>
    /// Fix round 3, ruling 1: <see cref="GitWorktreeWorkspaceProvider.ClaimAsync"/>'s own atomicity, tested directly
    /// rather than only through the much noisier full <see cref="P1x_the_same_run_id_against_two_repositories_leaves_exactly_one_live_workspace"/>.
    /// That end-to-end test's own downstream git work turned out to mask a broken claim for the cross-repository
    /// case specifically: with the claim's <see cref="FileMode.CreateNew"/> reverted to a non-atomic
    /// <see cref="FileMode.Create"/> during red verification, several racers can still win the claim step, but
    /// every extra winner then collides with the others on <c>git worktree add</c> against the one shared
    /// <see cref="RunWorkspace.Root"/> the run id implies — a second, independent, purely coincidental point of
    /// convergence that has nothing to do with the ownership claim itself, and that reliably absorbed the broken
    /// claim across every red trial attempted (12- and 20-way, repeated) without <see cref="P1x_the_same_run_id_against_two_repositories_leaves_exactly_one_live_workspace"/>
    /// ever failing. Calling <see cref="GitWorktreeWorkspaceProvider.ClaimAsync"/> directly removes that
    /// downstream noise entirely — no mirror, no worktree, just the sidecar file race — and, having no real git
    /// work to wait on, can run many more trials in the same time, each with more racers, which is what actually
    /// exposes the break reliably (see the report for the red observed this way).
    /// </summary>
    [Fact]
    public async Task ClaimAsync_lets_exactly_one_racing_call_win_across_repositories()
    {
        const int trials = 30;
        const int racersPerTrial = 10;
        var provider = Provider(out _);

        for (var trial = 0; trial < trials; trial++)
        {
            var runId = Guid.NewGuid();
            var barrier = new Barrier(racersPerTrial);
            var tasks = new Task<Result<RunWorkspace, AgentError>>[racersPerTrial];
            for (var i = 0; i < racersPerTrial; i++)
            {
                var request = new RunWorkspaceRequest(runId, $"repo-{i}", "https://example.invalid/repo.git", "main", $"manufacture/{runId}", null);
                var root = $"/irrelevant/root-{i}";
                tasks[i] = Task.Run(async () =>
                {
                    barrier.SignalAndWait();
                    return await provider.ClaimAsync(request, root, CancellationToken.None);
                });
            }

            var results = await Task.WhenAll(tasks);
            var (successCount, _) = CountSuccesses(results);
            successCount.Should().Be(1, $"trial {trial}: exactly one of {racersPerTrial} concurrent claims for the same run id, across that many different repositories, must win");
        }
    }

    /// <summary>
    /// Fix round 3, ruling 3: the isolation files <see cref="GitCli"/>'s constructor clears must never race under
    /// concurrent construction. Before this fix, review found 24 of 240 parallel constructions on a shared
    /// <see cref="GitWorkspaceOptions.DataRoot"/> threw <see cref="DirectoryNotFoundException"/> — the old
    /// delete-then-recreate pattern let one instance's delete land between another's own delete and its
    /// <c>CreateDirectory</c>. This provider is registered as a DI singleton, so a losing instance failing here
    /// fails an entire host at startup. <see cref="Task.Run{TResult}(Func{TResult})"/> is used here deliberately —
    /// not to fake async over a synchronous API (the rule this codebase forbids it for), but to generate genuine
    /// concurrent load from many real threads, the only way to reproduce a filesystem race at all.
    /// </summary>
    [Fact]
    public async Task Constructing_many_providers_on_one_shared_data_root_concurrently_never_throws()
    {
        var exceptions = new ConcurrentBag<Exception>();
        var barrier = new Barrier(150);
        // A dedicated thread per constructor: 150 threads blocked on one Barrier would starve the thread pool.
        var tasks = Enumerable.Range(0, 150).Select(_ => Task.Factory.StartNew(
            () =>
            {
                barrier.SignalAndWait();
                ConstructProviderCatching(exceptions);
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default));

        await Task.WhenAll(tasks);

        exceptions.Should().BeEmpty("no provider construction on a shared DataRoot may throw, since this provider is a DI singleton and a losing instance would fail an entire host at startup");
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
        // Directory.Exists alone would not prove ws1 still works as a git checkout, only that a directory with
        // that name is present. Git throws when git status exits nonzero, so the call itself is the check.
        _ = Git(ws1.Root, "status");
    }

    /// <summary>
    /// Fix round 3, minor: an Indeterminate mirror validation must carry git's own extracted error detail, not just
    /// "could not validate" — an operator reading <see cref="AgentError.ToString"/> needs to see e.g. "dubious
    /// ownership" to know what actually happened, not just that validation was inconclusive.
    /// </summary>
    [Fact]
    public async Task An_indeterminate_mirror_validation_reports_gits_own_error_detail()
    {
        using var remote = LocalGitRemote.Create();
        var normalProvider = Provider(out _);
        var runId1 = Guid.NewGuid();
        await normalProvider.CreateAsync(
            new RunWorkspaceRequest(runId1, "sandbox", remote.Url, "main", $"manufacture/{runId1}", null),
            CancellationToken.None);

        var dubiousOwnershipGit = WriteDubiousOwnershipGit(_temp);
        var dubiousProvider = new GitWorktreeWorkspaceProvider(
            new GitWorkspaceOptions { DataRoot = _dataRoot, GitExecutable = dubiousOwnershipGit },
            [],
            NullLogger<GitWorktreeWorkspaceProvider>.Instance,
            TimeProvider.System);

        var runId2 = Guid.NewGuid();
        var result = await dubiousProvider.CreateAsync(
            new RunWorkspaceRequest(runId2, "sandbox", remote.Url, "main", $"manufacture/{runId2}", null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.ToString().Should().Contain("dubious ownership", "an operator must see git's own reason the validation was inconclusive");
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
        // Git throws when git status exits nonzero, so the call itself checks ws1 is still a working checkout.
        _ = Git(ws1.Root, "status");
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

    /// <summary>
    /// Fix round 2, ruling 6; sharpened in fix round 3 after review found the original test not falsifiable:
    /// changing the catch block's <c>continue</c> to <c>break</c> stayed green, because directory enumeration
    /// order happened to put the one real sidecar first, so <c>break</c> on the garbage file right after it lost
    /// nothing. A first attempt at fixing this used exactly two real workspaces plus one garbage sidecar named with
    /// a low, all-zero-GUID prefix to bias it toward sorting first — that reliably caught the mutation on Windows,
    /// but not on Linux, where <c>Directory.EnumerateFiles</c>' order is unrelated to filename content (ext4 does
    /// not enumerate alphabetically) and the garbage file could just as easily land last, where a <c>break</c> there
    /// loses nothing. Several real workspaces and several garbage sidecars fixes this without depending on
    /// enumeration order at all: for <c>break</c> to lose no real workspace, every one of <see cref="GarbageSidecarCount"/>
    /// garbage files would need to land after all <see cref="RealWorkspaceCount"/> real ones — implausible under any
    /// real enumeration order, unlike the single-garbage-file case where it was merely uncommon.
    /// </summary>
    private const int RealWorkspaceCount = 5;

    private const int GarbageSidecarCount = 5;

    [Fact]
    public async Task ListAsync_skips_corrupt_sidecars_and_returns_every_real_workspace()
    {
        using var remote = LocalGitRemote.Create();
        var provider = Provider(out _);

        var expectedRunIds = new List<Guid>();
        for (var i = 0; i < RealWorkspaceCount; i++)
        {
            var runId = Guid.NewGuid();
            await provider.CreateAsync(
                new RunWorkspaceRequest(runId, "sandbox", remote.Url, "main", $"manufacture/{runId}", null),
                CancellationToken.None);
            expectedRunIds.Add(runId);
        }

        for (var i = 0; i < GarbageSidecarCount; i++)
        {
            var garbagePath = Path.Combine(_dataRoot, "runs", $"{Guid.NewGuid()}.workspace.json");
            File.WriteAllText(garbagePath, "{not json");
        }

        var listed = await provider.ListAsync(CancellationToken.None);

        listed.Select(w => w.RunId).Should().BeEquivalentTo(expectedRunIds, "every real workspace must survive skipping the unreadable sidecars, regardless of enumeration order");
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

    /// <summary>
    /// Counts how many of two racing <see cref="GitWorktreeWorkspaceProvider.CreateAsync"/> results succeeded, and
    /// returns the workspace of the last one that did — a plain loop instead of LINQ's <c>Count</c>/<c>Single</c>,
    /// which this codebase's analyzer forbids inside the trial loop <see cref="P1y_two_provider_instances_racing_the_same_run_leave_exactly_one_live_workspace"/>
    /// and <see cref="P1x_the_same_run_id_against_two_repositories_leaves_exactly_one_live_workspace"/> run (ZA0601:
    /// an allocation on every iteration).
    /// </summary>
    private static (int SuccessCount, RunWorkspace? Winner) CountSuccesses(Result<RunWorkspace, AgentError>[] results)
    {
        var successCount = 0;
        RunWorkspace? winner = null;
        foreach (var result in results)
        {
            if (result.IsSuccess)
            {
                successCount++;
                winner = result.Value;
            }
        }

        return (successCount, winner);
    }

    /// <summary>
    /// Constructs a <see cref="GitWorktreeWorkspaceProvider"/> over <see cref="_dataRoot"/>, recording any
    /// exception into <paramref name="exceptions"/> instead of letting it end the calling thread — for the
    /// concurrent-construction race test, where the assertion is about whether any construction throws at all.
    /// </summary>
    private void ConstructProviderCatching(ConcurrentBag<Exception> exceptions)
    {
        try
        {
            _ = new GitWorktreeWorkspaceProvider(
                new GitWorkspaceOptions { DataRoot = _dataRoot },
                [],
                NullLogger<GitWorktreeWorkspaceProvider>.Instance,
                TimeProvider.System);
        }
        catch (Exception ex)
        {
            exceptions.Add(ex);
        }
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
        // Checked first: git cannot even start in a directory that is gone, which throws rather than answering.
        if (!Directory.Exists(path))
        {
            return false;
        }

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
            // $$# is ambiguous past 9 positional parameters (POSIX expands $13 as ${1}3, not ${13}) — walk the
            // parameters instead of indexing by count, which is correct for any argument count.
            "    for last; do :; done\n" +
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
    /// Writes a git wrapper that answers <c>--version</c> normally, fails <c>rev-parse --is-bare-repository</c>
    /// immediately with a "dubious ownership" message on standard error (git's own real wording when a repository
    /// is owned by a different user than the one running git), and proxies everything else to the real git — for
    /// the Indeterminate-detail test: the failure must be immediate, not a timeout, so the resulting
    /// <see cref="AgentError"/> is built from <see cref="GitWorktreeWorkspaceProvider.ExtractErrorDetail"/> rather
    /// than a generic "timed out" message.
    /// </summary>
    private static string WriteDubiousOwnershipGit(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(dir, "dubious-ownership-git-" + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(path,
                "@echo off\r\n" +
                "echo %* | findstr /C:\"--version\" >nul\r\n" +
                "if %errorlevel%==0 (\r\n" +
                "    echo git version 2.43.0\r\n" +
                "    exit /b 0\r\n" +
                ")\r\n" +
                "echo %* | findstr /C:\"--is-bare-repository\" >nul\r\n" +
                "if %errorlevel%==0 (\r\n" +
                "    echo fatal: detected dubious ownership in repository 1>&2\r\n" +
                "    exit /b 128\r\n" +
                ")\r\n" +
                "git %*\r\n" +
                "exit /b %errorlevel%\r\n");
            return path;
        }

        var scriptPath = Path.Combine(dir, "dubious-ownership-git-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "case \"$*\" in\n" +
            "  *--version*)\n" +
            "    echo \"git version 2.43.0\"\n" +
            "    exit 0\n" +
            "    ;;\n" +
            "  *--is-bare-repository*)\n" +
            "    echo \"fatal: detected dubious ownership in repository\" >&2\n" +
            "    exit 128\n" +
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
            // Directories too: a test may plant a read-only directory, which blocks deleting its children on Linux.
            foreach (var directory in Directory.EnumerateDirectories(_temp, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(directory, FileAttributes.Directory);
            }

            foreach (var file in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_temp, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
