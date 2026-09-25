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

    /// <summary>Fix round 1, ruling 3: a global gitconfig's core.hooksPath must never run, because GIT_CONFIG_GLOBAL isolation means the host gitconfig that sets it is never read (and, redundantly, because core.hooksPath is also forced empty by -c on every call).</summary>
    [Fact]
    public async Task A_global_hooksPath_hook_does_not_run_during_checkout()
    {
        using var remote = LocalGitRemote.Create();
        var fakeHome = Directory.CreateTempSubdirectory("thalos-fake-home-").FullName;
        var hooksDir = Directory.CreateTempSubdirectory("thalos-fake-hooks-").FullName;
        var marker = Path.Combine(_temp, "hook-ran.marker");
        try
        {
            WriteHookScript(hooksDir, marker);
            File.WriteAllText(Path.Combine(fakeHome, ".gitconfig"), $"[core]\n\thooksPath = {hooksDir.Replace('\\', '/')}\n");

            await WithFakeHomeAsync(fakeHome, async () =>
            {
                var runId = Guid.NewGuid();
                var result = await Provider(out _).CreateAsync(
                    new RunWorkspaceRequest(runId, "sandbox", remote.Url, "main", $"manufacture/{runId}", null),
                    CancellationToken.None);
                result.IsSuccess.Should().BeTrue();
            });

            File.Exists(marker).Should().BeFalse("a host's global hooksPath must never run against a worktree this provider checks out");
        }
        finally
        {
            Directory.Delete(fakeHome, recursive: true);
            Directory.Delete(hooksDir, recursive: true);
        }
    }

    /// <summary>Fix round 1, ruling 3: protocol.ext.allow=always in a host's global gitconfig must not make an ext:: remote run, because that global gitconfig is never read.</summary>
    [Fact]
    public async Task A_permissive_host_protocol_config_does_not_allow_an_ext_remote()
    {
        var fakeHome = Directory.CreateTempSubdirectory("thalos-fake-home-").FullName;
        var marker = Path.Combine(_temp, "ext-ran.marker");
        try
        {
            File.WriteAllText(Path.Combine(fakeHome, ".gitconfig"), "[protocol \"ext\"]\n\tallow = always\n");

            await WithFakeHomeAsync(fakeHome, async () =>
            {
                var runId = Guid.NewGuid();
                // No quoting: a marker path is guaranteed not to contain spaces, so "ext::touch <path>" is
                // git's own plain (unquoted) word-split of the ext:: remote string — a quoted form here would
                // depend on the exact quoting git's ext helper expects, which is a separate concern from what
                // this test verifies (that ext:: is refused regardless of the host's protocol.ext.allow).
                var result = await Provider(out _).CreateAsync(
                    new RunWorkspaceRequest(runId, "sandbox", $"ext::touch {marker.Replace('\\', '/')}", "main", $"manufacture/{runId}", null),
                    CancellationToken.None);
                result.IsFailure.Should().BeTrue();
            });

            File.Exists(marker).Should().BeFalse("protocol.allow=never must hold even though the host's own (never-read) gitconfig allows ext:: specifically");
        }
        finally
        {
            Directory.Delete(fakeHome, recursive: true);
        }
    }

    /// <summary>
    /// Fix round 1, ruling 1 (CRITICAL): a command that times out reports a failure result — it never lets a
    /// <see cref="TimeoutException"/> escape <see cref="GitWorktreeWorkspaceProvider.CreateAsync"/> and skip its
    /// own cleanup. A git that hangs forever is killed once <see cref="GitWorkspaceOptions.CommandTimeout"/>
    /// elapses, and nothing is left behind at the mirror's real path.
    /// </summary>
    [Fact]
    public async Task A_command_timeout_is_a_failure_result_not_a_thrown_exception_and_leaves_no_half_mirror()
    {
        using var remote = LocalGitRemote.Create();
        var hangingGit = WriteHangingGit(_temp);
        var runId = Guid.NewGuid();

        var provider = new GitWorktreeWorkspaceProvider(
            new GitWorkspaceOptions { DataRoot = _dataRoot, GitExecutable = hangingGit, CommandTimeout = TimeSpan.FromMilliseconds(300) },
            [],
            NullLogger<GitWorktreeWorkspaceProvider>.Instance,
            TimeProvider.System);

        // If a timeout instead threw, this await would surface it as a test-ending exception rather than a Result.
        var result = await provider.CreateAsync(
            new RunWorkspaceRequest(runId, "sandbox", remote.Url, "main", $"manufacture/{runId}", null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        Directory.Exists(Path.Combine(_dataRoot, "mirrors", "sandbox")).Should().BeFalse("a timed-out first clone must not leave a half-cloned mirror at its final path");
        Directory.Exists(Path.Combine(_dataRoot, "runs", runId.ToString())).Should().BeFalse();
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

    /// <summary>Sets HOME and USERPROFILE for the duration of <paramref name="action"/>, restoring both afterward — a fake "host" identity for the environment-isolation probes.</summary>
    private static async Task WithFakeHomeAsync(string fakeHome, Func<Task> action)
    {
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        Environment.SetEnvironmentVariable("HOME", fakeHome);
        Environment.SetEnvironmentVariable("USERPROFILE", fakeHome);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("USERPROFILE", originalUserProfile);
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

    /// <summary>Writes a fake git executable that ignores every argument and hangs forever — for the timeout test.</summary>
    private static string WriteHangingGit(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(dir, "hanging-git-" + Guid.NewGuid().ToString("N") + ".cmd");
            // A console-free sleep: `timeout` refuses to run without a real console, `ping` does not.
            File.WriteAllText(path, "@ping -n 9999 127.0.0.1 >nul\r\n");
            return path;
        }

        var scriptPath = Path.Combine(dir, "hanging-git-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(scriptPath, "#!/bin/sh\nsleep 9999\n");
        File.SetUnixFileMode(scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return scriptPath;
    }

    /// <summary>Writes a fake git executable that ignores every argument and just echoes <paramref name="versionLine"/> — for the version-gate test.</summary>
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
