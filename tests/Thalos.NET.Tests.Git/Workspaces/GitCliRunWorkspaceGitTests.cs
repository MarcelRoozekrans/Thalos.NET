using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Git;
using Thalos.Git.Workspaces;
using Thalos.Workspaces;

namespace Thalos.Tests.Git.Workspaces;

/// <summary>
/// Tests for <see cref="GitCliRunWorkspaceGit"/> against a real worktree, made with A6's own
/// <see cref="LocalGitRemote"/> and a real <see cref="GitWorktreeWorkspaceProvider"/> — no fake transport, no
/// mocked git, exactly as <see cref="GitWorktreeWorkspaceProviderTests"/> tests A6 itself.
/// </summary>
public sealed class GitCliRunWorkspaceGitTests : IDisposable
{
    private static readonly GitAuthor TestAuthor = new("Test Author", "test-author@example.invalid");

    private readonly string _temp = Directory.CreateTempSubdirectory("thalos-run-git-").FullName;
    private readonly GitWorkspaceOptions _options;
    private readonly GitCliRunWorkspaceGit _git;
    private LocalGitRemote? _remote;

    public GitCliRunWorkspaceGitTests()
    {
        _options = new GitWorkspaceOptions { DataRoot = Path.Combine(_temp, "data") };
        _git = new GitCliRunWorkspaceGit(_options, NullLogger<GitCliRunWorkspaceGit>.Instance);
    }

    [Fact]
    public async Task An_excluded_path_stays_uncommitted_and_a_paths_commit_takes_only_it()
    {
        var ws = await WorktreeAsync(("AGENT.md", "Run dotnet test."));
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "class C {}");
        File.WriteAllText(Path.Combine(ws.Root, "AGENT.md"), "Updated.");

        var code = await _git.CommitAsync(ws, new GitCommitRequest { Message = "code", Author = TestAuthor, ExcludePaths = ["AGENT.md"] }, CancellationToken.None);
        var agent = await _git.CommitAsync(ws, new GitCommitRequest { Message = "agent", Author = TestAuthor, Paths = ["AGENT.md"] }, CancellationToken.None);

        code.Value.Created.Should().BeTrue();
        // Moved ahead of agent.Value.Created (fix round 1, ruling 5): this is the assertion the exclude/paths
        // scoping is actually about, so the red for a missing reset of ExcludePaths must land here, not on a
        // downstream side effect of the same bug.
        FilesIn(ws, code.Value.Sha).Should().Contain("code.cs").And.NotContain("AGENT.md");
        agent.Value.Created.Should().BeTrue();
        FilesIn(ws, agent.Value.Sha).Should().Equal("AGENT.md");
    }

    [Fact]
    public async Task A_repeated_commit_with_nothing_new_creates_nothing()
    {
        var ws = await WorktreeAsync();

        var result = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor, ExcludePaths = ["AGENT.md"] }, CancellationToken.None);

        // Split (fix round 1, ruling 5): accessing .Value on a failed Result throws, which is not a red on the
        // named assertion below. Checking IsSuccess first turns a broken "diff --cached --quiet" check into a
        // clean assertion failure here instead.
        result.IsSuccess.Should().BeTrue();
        result.Value.Created.Should().BeFalse();
    }

    [Fact]
    public async Task Diff_stat_lists_every_file_changed_since_the_merge_base_with_line_counts()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "a\nb\n");
        await _git.CommitAsync(ws, new GitCommitRequest { Message = "one", Author = TestAuthor }, CancellationToken.None);
        File.WriteAllText(Path.Combine(ws.Root, "other.cs"), "c\n");
        await _git.CommitAsync(ws, new GitCommitRequest { Message = "two", Author = TestAuthor }, CancellationToken.None);

        var stat = (await _git.DiffStatAsync(ws, CancellationToken.None)).Value;
        stat.Should().BeEquivalentTo([new GitFileChange("code.cs", 2, 0), new GitFileChange("other.cs", 1, 0)]);
    }

    /// <summary>
    /// Minors: <c>--no-renames</c> means a rename shows up as a delete of the old path plus an add of the new one.
    /// The local <c>origin/main</c> tracking ref is advanced to the first commit before the rename, so
    /// <see cref="GitCliRunWorkspaceGit.DiffStatAsync"/>'s own merge-base diff isolates just the rename — otherwise
    /// the merge base would sit before <c>code.cs</c> ever existed, and its whole lifetime (add then rename) would
    /// collapse into a single "renamed.cs added" record with no delete record for <c>code.cs</c> to disable.
    /// </summary>
    [Fact]
    public async Task Diff_stat_reports_a_rename_as_a_delete_and_an_add_since_renames_are_disabled()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "a\nb\nc\n");
        await _git.CommitAsync(ws, new GitCommitRequest { Message = "one", Author = TestAuthor }, CancellationToken.None);
        Git(ws.Root, "update-ref refs/remotes/origin/main HEAD");
        File.Move(Path.Combine(ws.Root, "code.cs"), Path.Combine(ws.Root, "renamed.cs"));
        await _git.CommitAsync(ws, new GitCommitRequest { Message = "two", Author = TestAuthor }, CancellationToken.None);

        var stat = (await _git.DiffStatAsync(ws, CancellationToken.None)).Value;

        stat.Should().BeEquivalentTo([new GitFileChange("code.cs", 0, 3), new GitFileChange("renamed.cs", 3, 0)]);
    }

    /// <summary>Minors: <c>-z</c> means a path with spaces, parentheses and non-ASCII characters parses correctly, unquoted.</summary>
    [Fact]
    public async Task Diff_stat_reports_a_path_containing_special_characters()
    {
        var ws = await WorktreeAsync();
        const string name = "a b (special) - 日本語.cs";
        File.WriteAllText(Path.Combine(ws.Root, name), "x\n");

        await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None);
        var stat = (await _git.DiffStatAsync(ws, CancellationToken.None)).Value;

        stat.Should().BeEquivalentTo([new GitFileChange(name, 1, 0)]);
    }

    [Fact]
    public async Task Push_sends_the_run_branch_to_its_remote_and_a_repeat_changes_nothing()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "x");
        var sha = (await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None)).Value.Sha;

        (await _git.PushAsync(ws, CancellationToken.None)).IsSuccess.Should().BeTrue();
        // TryHeadOf, not HeadOf (fix round 1, ruling 5): a wrong-branch push leaves ws.Branch entirely absent on
        // the remote, and HeadOf throws on that; TryHeadOf returns null, landing the red on Should().Be(sha).
        _remote!.TryHeadOf(ws.Branch).Should().Be(sha);
        (await _git.PushAsync(ws, CancellationToken.None)).IsSuccess.Should().BeTrue();
        _remote!.TryHeadOf(ws.Branch).Should().Be(sha);
    }

    /// <summary>Push scope ruling: a run's branch equal to the repository's default branch is refused, and the remote's default branch is never touched.</summary>
    [Fact]
    public async Task Pushing_a_branch_equal_to_the_default_branch_is_refused_and_the_remote_is_unchanged()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "x");
        await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None);
        // The mirror's initial bare clone already holds a real local refs/heads/<DefaultBranch> (a bare clone
        // copies every branch directly, before CloneMirrorAsync reconfigures future fetches under refs/remotes/
        // origin/*), so checking it out here makes HEAD really point at refs/heads/<DefaultBranch> — isolating
        // this refusal from the separate symbolic-ref check below, which a workspace merely relabelled via "with"
        // would trip on its own.
        Git(ws.Root, $"checkout {ws.DefaultBranch}");
        var mainBefore = _remote!.TryHeadOf(ws.DefaultBranch);

        var pushed = await _git.PushAsync(ws with { Branch = ws.DefaultBranch }, CancellationToken.None);

        pushed.IsFailure.Should().BeTrue();
        _remote!.TryHeadOf(ws.DefaultBranch).Should().Be(mainBefore);
    }

    /// <summary>Push scope ruling: the push argv is exactly the explicit two-sided refspec, with no <c>--force</c> and no <c>--tags</c>.</summary>
    [Fact]
    public async Task Push_argv_holds_exactly_the_explicit_refspec_with_no_force_and_no_tags()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "x");
        await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None);

        var captureFile = Path.Combine(_temp, "push-argv-capture.log");
        File.WriteAllText(captureFile, string.Empty);
        var spyGit = WriteSpyGit(_temp, captureFile);
        var spyOptions = new GitWorkspaceOptions { DataRoot = _options.DataRoot, GitExecutable = spyGit, CommandTimeout = _options.CommandTimeout };
        var git = new GitCliRunWorkspaceGit(spyOptions, NullLogger<GitCliRunWorkspaceGit>.Instance);

        (await git.PushAsync(ws, CancellationToken.None)).IsSuccess.Should().BeTrue();

        var pushLine = File.ReadAllLines(captureFile).Single(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("push", StringComparer.Ordinal));
        var refspec = $"refs/heads/{ws.Branch}:refs/heads/{ws.Branch}";
        pushLine.Should().Contain(refspec).And.Contain(ws.Remote);
        pushLine.Should().NotContain("--force").And.NotContain("--tags");
    }

    /// <summary>Push scope ruling: pushing straight to the workspace's URL writes nothing into the mirror's shared config.</summary>
    [Fact]
    public async Task The_mirrors_config_is_unchanged_after_a_push()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "x");
        await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None);
        var configPath = Path.Combine(MirrorOf(), "config");
        var configBefore = File.ReadAllText(configPath);

        (await _git.PushAsync(ws, CancellationToken.None)).IsSuccess.Should().BeTrue();

        File.ReadAllText(configPath).Should().Be(configBefore);
    }

    [Fact]
    public async Task Push_asks_the_credential_source_for_the_runs_remote_and_never_stores_the_header()
    {
        var source = new RecordingCredentialSource(new GitCredentials("x-access-token", "secret-value"));
        var git = new GitCliRunWorkspaceGit(_options, NullLogger<GitCliRunWorkspaceGit>.Instance, source);
        var ws = await WorktreeAsync();

        await git.PushAsync(ws, CancellationToken.None); // a local remote ignores the header; the push still succeeds

        source.RequestedUrls.Should().Equal(ws.Remote);
        Git(ws.Root, "config --get-all http.extraHeader").Should().BeEmpty();
    }

    [Fact]
    public async Task A_push_to_a_missing_remote_fails_naming_git_and_not_the_secret()
    {
        var git = new GitCliRunWorkspaceGit(_options, NullLogger<GitCliRunWorkspaceGit>.Instance, new RecordingCredentialSource(new GitCredentials("x-access-token", "secret-value")));
        var ws = await WorktreeAsync();
        _remote!.Delete();

        var pushed = await git.PushAsync(ws, CancellationToken.None);

        pushed.IsFailure.Should().BeTrue();
        pushed.Error.ToString().Should().NotContain("secret-value").And.NotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:secret-value")));
    }

    /// <summary>
    /// Fix round 1, item 5: a git that actually echoes the secret env value to stderr (rather than a real, local
    /// file remote that never mentions it at all) is the only way to falsify the <c>Scrub</c> call itself.
    /// </summary>
    [Fact]
    public async Task Push_scrubs_the_credential_header_out_of_an_error_that_echoes_it()
    {
        var ws = await WorktreeAsync();
        var echoingGit = WriteEchoingGit(_temp);
        const string token = "scrub-red-token-value";
        var echoOptions = new GitWorkspaceOptions { DataRoot = _options.DataRoot, GitExecutable = echoingGit, CommandTimeout = _options.CommandTimeout };
        var git = new GitCliRunWorkspaceGit(echoOptions, NullLogger<GitCliRunWorkspaceGit>.Instance, new RecordingCredentialSource(new GitCredentials("x-access-token", token)));

        var pushed = await git.PushAsync(ws, CancellationToken.None);

        pushed.IsFailure.Should().BeTrue();
        var expectedHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));
        pushed.Error.ToString().Should().NotContain(expectedHeader).And.NotContain(token);
    }

    /// <summary>Commit scope ruling: a failed commit attempt must never leave files staged for the next commit to pick up.</summary>
    [Fact]
    public async Task A_failed_commit_does_not_leak_staged_files_into_the_next_commit()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "other.cs"), "leftover");
        File.WriteAllText(Path.Combine(ws.Root, "AGENT.md"), "Updated.");

        // git refuses an empty commit message, so this stages everything (via add -A) but never commits it.
        var failed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "", Author = TestAuthor }, CancellationToken.None);
        failed.IsFailure.Should().BeTrue();

        var agent = await _git.CommitAsync(ws, new GitCommitRequest { Message = "agent", Author = TestAuthor, Paths = ["AGENT.md"] }, CancellationToken.None);

        agent.Value.Created.Should().BeTrue();
        FilesIn(ws, agent.Value.Sha).Should().Equal("AGENT.md");
    }

    /// <summary>
    /// Commit scope ruling: <c>--literal-pathspecs</c> means <c>Paths</c> is matched by its exact name, never as a
    /// glob. <c>"AGENT.m[d]"</c> is a git glob (a character class matching the single character <c>d</c>) that
    /// would match <c>AGENT.md</c> under git's default pathspec magic — the exact shape of the glob bug — but is
    /// not itself a real file, and its brackets are valid on every filesystem this suite runs on (unlike <c>*</c> or
    /// <c>?</c>, both reserved on Windows, which would make <see cref="WorkspacePath.Resolve"/> refuse the path for
    /// an unrelated reason before git is ever involved). With literal pathspecs, <c>git add</c> reports no match and
    /// the call fails outright — never silently staging (and so committing) every file including <c>AGENT.md</c>.
    /// </summary>
    [Fact]
    public async Task A_glob_looking_path_is_taken_literally_and_never_matches_a_real_file()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "AGENT.md"), "Updated.");
        var headBefore = Git(ws.Root, "rev-parse HEAD");

        var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor, Paths = ["AGENT.m[d]"] }, CancellationToken.None);

        committed.IsFailure.Should().BeTrue("'AGENT.m[d]' must be taken as a literal, nonexistent filename, never as a glob matching AGENT.md");
        Git(ws.Root, "rev-parse HEAD").Should().Be(headBefore, "no commit must be made when the literal path matches nothing");
    }

    /// <summary>
    /// Commit scope ruling: <c>--literal-pathspecs</c> means <c>ExcludePaths</c> is matched by its exact name too.
    /// Unlike <c>git add</c>, <c>git reset</c> does not error on a pathspec that matches nothing — it simply
    /// excludes nothing — so with the glob bug fixed, <c>AGENT.md</c>'s own real change survives and is committed,
    /// where the bug would have glob-matched <c>"AGENT.m[d]"</c> against <c>AGENT.md</c> and silently dropped it.
    /// </summary>
    [Fact]
    public async Task A_glob_looking_exclude_path_is_taken_literally_and_does_not_exclude_a_real_file()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "AGENT.md"), "Updated.");

        var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor, ExcludePaths = ["AGENT.m[d]"] }, CancellationToken.None);

        committed.IsSuccess.Should().BeTrue();
        committed.Value.Created.Should().BeTrue("'AGENT.m[d]' must be taken as a literal, nonexistent filename, never as a glob that excludes AGENT.md");
        FilesIn(ws, committed.Value.Sha).Should().Contain("AGENT.md");
    }

    /// <summary>
    /// Fix round 1, item 6: covers <c>pre-commit</c>, <c>commit-msg</c> and <c>post-commit</c>, and plants a
    /// competing <c>core.hooksPath</c> directly in the mirror's own tracked config (which a worktree shares with
    /// its mirror) to prove <see cref="GitCli"/>'s own <c>-c core.hooksPath=...</c> — stated on every command line —
    /// always wins over it. Never touches this test process's own <c>HOME</c>.
    /// </summary>
    [Fact]
    public async Task No_hook_ever_fires_on_a_commit_even_when_the_mirror_configures_its_own_hooksPath()
    {
        var ws = await WorktreeAsync();
        var mirror = MirrorOf();
        var competingHooksDir = Path.Combine(_temp, "competing-hooks");
        Directory.CreateDirectory(competingHooksDir);
        LocalGitRemote.RunGit(mirror, "config", "core.hooksPath", competingHooksDir.Replace('\\', '/'));

        var hooks = new (string Name, string Marker)[]
        {
            ("pre-commit", Path.Combine(_temp, "pre-commit.marker")),
            ("commit-msg", Path.Combine(_temp, "commit-msg.marker")),
            ("post-commit", Path.Combine(_temp, "post-commit.marker")),
        };
        var defaultHooksDir = Path.Combine(mirror, "hooks");
        Directory.CreateDirectory(defaultHooksDir);
        foreach (var (name, marker) in hooks)
        {
            WriteHookScript(competingHooksDir, name, marker);
            WriteHookScript(defaultHooksDir, name, marker);
        }

        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "class C {}");
        var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None);

        committed.Value.Created.Should().BeTrue();
        foreach (var (_, marker) in hooks)
        {
            File.Exists(marker).Should().BeFalse($"'{marker}' must never be created: core.hooksPath isolation must beat both the mirror's own configured hooksPath and the default hooks directory");
        }
    }

    /// <summary>
    /// Decisions ruling 2: the credential header must never reach argv on a push, verified here with a "spy" git
    /// wrapper that logs every argv it receives and then execs the real git, so the push still succeeds normally —
    /// the same technique A6's own <c>Credentials_never_appear_on_the_git_command_line</c> test uses for a clone.
    /// </summary>
    [Fact]
    public async Task Push_never_puts_the_credential_header_on_the_git_command_line()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "x");
        await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", Author = TestAuthor }, CancellationToken.None);

        var captureFile = Path.Combine(_temp, "argv-capture.log");
        File.WriteAllText(captureFile, string.Empty);
        var spyGit = WriteSpyGit(_temp, captureFile);
        const string token = "super-secret-push-token-value";
        var spyOptions = new GitWorkspaceOptions { DataRoot = _options.DataRoot, GitExecutable = spyGit, CommandTimeout = _options.CommandTimeout };
        var git = new GitCliRunWorkspaceGit(spyOptions, NullLogger<GitCliRunWorkspaceGit>.Instance, new RecordingCredentialSource(new GitCredentials("x-access-token", token)));

        var pushed = await git.PushAsync(ws, CancellationToken.None);

        pushed.IsSuccess.Should().BeTrue();
        var captured = File.ReadAllText(captureFile);
        captured.Should().NotContain(token, "the credential value must never reach argv");
        captured.Should().NotContain(
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}")),
            "the encoded credential header must never reach argv either");
    }

    /// <summary>Creates a fresh remote and a worktree for a new run id, over this test's own <see cref="_options"/>.</summary>
    private async Task<RunWorkspace> WorktreeAsync(params (string Name, string Content)[] seed)
    {
        _remote = LocalGitRemote.Create(seed);
        var provider = new GitWorktreeWorkspaceProvider(
            _options,
            [],
            NullLogger<GitWorktreeWorkspaceProvider>.Instance,
            TimeProvider.System);
        var runId = Guid.NewGuid();
        var created = await provider.CreateAsync(
            new RunWorkspaceRequest(runId, "sandbox", _remote.Url, "main", $"manufacture/{runId}", null),
            CancellationToken.None);
        return created.Value;
    }

    private string MirrorOf() => Path.Combine(_options.DataRoot, "mirrors", "sandbox");

    private static string[] FilesIn(RunWorkspace ws, string sha) =>
        Git(ws.Root, $"show --name-only --format= {sha}").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Runs git and returns trimmed stdout, without throwing on a nonzero exit — e.g. a "key not found" answer from <c>config --get-all</c>.</summary>
    private static string Git(string workingDirectory, string args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
        var stdOut = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return stdOut.Trim();
    }

    /// <summary>Writes a cross-platform hook named <paramref name="hookName"/> under <paramref name="hooksDir"/> that touches <paramref name="markerPath"/> if git ever runs it.</summary>
    private static void WriteHookScript(string hooksDir, string hookName, string markerPath)
    {
        var hookPath = Path.Combine(hooksDir, hookName);
        File.WriteAllText(hookPath, $"#!/bin/sh\ntouch \"{markerPath}\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
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

    /// <summary>
    /// Writes a git wrapper that, only for a <c>push</c> invocation, echoes <c>$GIT_CONFIG_VALUE_0</c> (the secret
    /// <see cref="GitCli"/>'s <c>secretConfig</c> mechanism carries) to stderr and exits 1 — a git that actually
    /// leaks the header into its own error output, rather than the real, local file remote every other push test
    /// uses, which never mentions it at all. Every other invocation passes straight through to the real git.
    /// </summary>
    private static string WriteEchoingGit(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(dir, "echo-git-" + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(path,
                "@echo off\r\n" +
                "setlocal\r\n" +
                "set ARGS=%*\r\n" +
                "echo %ARGS% | findstr /C:\" push \" >nul\r\n" +
                "if %errorlevel%==0 (\r\n" +
                "    echo %GIT_CONFIG_VALUE_0% 1>&2\r\n" +
                "    exit /b 1\r\n" +
                ")\r\n" +
                "git %*\r\n" +
                "exit /b %errorlevel%\r\n");
            return path;
        }

        var scriptPath = Path.Combine(dir, "echo-git-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "case \" $* \" in\n" +
            "  *\" push \"*)\n" +
            "    echo \"$GIT_CONFIG_VALUE_0\" 1>&2\n" +
            "    exit 1\n" +
            "    ;;\n" +
            "esac\n" +
            "exec git \"$@\"\n");
        File.SetUnixFileMode(scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return scriptPath;
    }

    /// <summary>Records every URL it is asked for credentials about, for <see cref="Push_asks_the_credential_source_for_the_runs_remote_and_never_stores_the_header"/>.</summary>
    private sealed class RecordingCredentialSource(GitCredentials credentials) : IGitCredentialSource
    {
        public List<string> RequestedUrls { get; } = [];

        public GitCredentials? GetCredentials(string remoteUrl)
        {
            RequestedUrls.Add(remoteUrl);
            return credentials;
        }
    }

    public void Dispose()
    {
        _remote?.Dispose();
        if (!Directory.Exists(_temp))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a lingering handle on a temp directory is not worth failing the test run over.
        }
    }
}
