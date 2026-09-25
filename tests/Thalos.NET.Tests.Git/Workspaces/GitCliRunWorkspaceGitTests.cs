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

        var code = await _git.CommitAsync(ws, new GitCommitRequest { Message = "code", ExcludePaths = ["AGENT.md"] }, CancellationToken.None);
        var agent = await _git.CommitAsync(ws, new GitCommitRequest { Message = "agent", Paths = ["AGENT.md"] }, CancellationToken.None);

        code.Value.Created.Should().BeTrue();
        agent.Value.Created.Should().BeTrue();
        FilesIn(ws, code.Value.Sha).Should().Contain("code.cs").And.NotContain("AGENT.md");
        FilesIn(ws, agent.Value.Sha).Should().Equal("AGENT.md");
    }

    [Fact]
    public async Task A_repeated_commit_with_nothing_new_creates_nothing()
    {
        var ws = await WorktreeAsync();
        (await _git.CommitAsync(ws, new GitCommitRequest { Message = "m", ExcludePaths = ["AGENT.md"] }, CancellationToken.None)).Value.Created.Should().BeFalse();
    }

    [Fact]
    public async Task Diff_stat_lists_every_file_changed_since_the_merge_base_with_line_counts()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "a\nb\n");
        await _git.CommitAsync(ws, new GitCommitRequest { Message = "one" }, CancellationToken.None);
        File.WriteAllText(Path.Combine(ws.Root, "other.cs"), "c\n");
        await _git.CommitAsync(ws, new GitCommitRequest { Message = "two" }, CancellationToken.None);

        var stat = (await _git.DiffStatAsync(ws, CancellationToken.None)).Value;
        stat.Should().BeEquivalentTo([new GitFileChange("code.cs", 2, 0), new GitFileChange("other.cs", 1, 0)]);
    }

    [Fact]
    public async Task Push_sends_the_run_branch_to_its_remote_and_a_repeat_changes_nothing()
    {
        var ws = await WorktreeAsync();
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "x");
        var sha = (await _git.CommitAsync(ws, new GitCommitRequest { Message = "m" }, CancellationToken.None)).Value.Sha;

        (await _git.PushAsync(ws, CancellationToken.None)).IsSuccess.Should().BeTrue();
        _remote!.HeadOf(ws.Branch).Should().Be(sha);
        (await _git.PushAsync(ws, CancellationToken.None)).IsSuccess.Should().BeTrue();
        _remote!.HeadOf(ws.Branch).Should().Be(sha);
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
    /// Decisions ruling 1: every git call this type makes goes through <see cref="GitCli"/>'s host-configuration
    /// isolation, which passes <c>-c core.hooksPath=&lt;an empty provider-owned directory&gt;</c> on every
    /// invocation. This plants a real <c>pre-commit</c> hook directly in the worktree's own repository (the bare
    /// mirror's <c>hooks</c> directory, which a worktree shares with its mirror) and proves it never runs on a
    /// commit <see cref="GitCliRunWorkspaceGit"/> makes.
    /// </summary>
    [Fact]
    public async Task A_repository_level_hook_never_fires_on_commit()
    {
        var ws = await WorktreeAsync();
        var hooksDir = Path.Combine(MirrorOf(), "hooks");
        Directory.CreateDirectory(hooksDir);
        var marker = Path.Combine(_temp, "hook-ran.marker");
        WriteHookScript(hooksDir, "pre-commit", marker);
        File.WriteAllText(Path.Combine(ws.Root, "code.cs"), "class C {}");

        var committed = await _git.CommitAsync(ws, new GitCommitRequest { Message = "m" }, CancellationToken.None);

        committed.Value.Created.Should().BeTrue();
        File.Exists(marker).Should().BeFalse("core.hooksPath isolation must prevent a repository-level hook from running on a commit made through GitCliRunWorkspaceGit");
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
        await _git.CommitAsync(ws, new GitCommitRequest { Message = "m" }, CancellationToken.None);

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
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
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

        using var process = System.Diagnostics.Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
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
