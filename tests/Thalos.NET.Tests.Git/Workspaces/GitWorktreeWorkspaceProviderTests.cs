using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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

    /// <summary>
    /// Builds a provider over <see cref="_dataRoot"/> with a recording observer, a null logger and, by default,
    /// the real <see cref="TimeProvider.System"/> — the helper's default is test convenience; the provider's own
    /// <c>clock</c> parameter is required (ruling R27).
    /// </summary>
    private GitWorktreeWorkspaceProvider Provider(out RecordingObserver observer, TimeProvider? clock = null)
    {
        observer = new RecordingObserver();
        return new GitWorktreeWorkspaceProvider(
            new GitWorkspaceOptions { DataRoot = _dataRoot },
            [observer],
            NullLogger<GitWorktreeWorkspaceProvider>.Instance,
            clock ?? TimeProvider.System);
    }

    private static RunWorkspaceRequest Request(LocalGitRemote remote, Guid runId) =>
        new(runId, "sandbox", remote.Url, "main", $"manufacture/{runId}", null);

    private string MirrorOf(string repository) => Path.Combine(_dataRoot, "mirrors", repository);

    private static string Git(string workingDirectory, string args) =>
        LocalGitRemote.RunGit(workingDirectory, args.Split(' ', StringSplitOptions.RemoveEmptyEntries));

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

    private sealed class RecordingObserver : IRunWorkspaceObserver
    {
        public List<RunWorkspace> Ready { get; } = [];

        public List<RunWorkspace> Removing { get; } = [];

        public ValueTask OnReadyAsync(RunWorkspace workspace, CancellationToken ct)
        {
            Ready.Add(workspace);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnRemovingAsync(RunWorkspace workspace, CancellationToken ct)
        {
            Removing.Add(workspace);
            return ValueTask.CompletedTask;
        }
    }
}
