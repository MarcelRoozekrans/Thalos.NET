using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Thalos.Git.Workspaces;
using Thalos.Mcp;
using Thalos.Tests.Git.Workspaces;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Tests.Mcp;

/// <summary>
/// The registry observing the real <see cref="GitWorktreeWorkspaceProvider"/> over a real git remote: the
/// combination the host runs, where a removal's window between telling observers and deleting the record is real.
/// </summary>
public sealed class RunMcpServerRegistryWithGitProviderTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("thalos-run-mcp-git-").FullName;
    private readonly List<IAsyncDisposable> _disposables = [];

    /// <summary>
    /// Review probe P5, A9 fix round 2: a readiness lookup made after the registry has stopped a run's servers, while
    /// the provider is still removing the worktree, must not start an orphan server that holds the worktree as its
    /// working directory.
    /// </summary>
    [Fact]
    public async Task A_readiness_lookup_during_the_git_side_of_a_removal_starts_no_orphan_server()
    {
        using var remote = LocalGitRemote.Create();
        var registryObserver = new ForwardingObserver();
        var lateLookup = new LookupWhenRemovingObserver();
        var provider = new GitWorktreeWorkspaceProvider(
            new GitWorkspaceOptions { DataRoot = Path.Combine(_root, "data") },
            [registryObserver, lateLookup],
            NullLogger<GitWorktreeWorkspaceProvider>.Instance,
            TimeProvider.System);
        var definition = McpServerFixture.Definition();
        definition.RunScoped = new RunScopedMcpDefinition { Args = [McpServerFixture.ServerDll] };
        var registry = new RunMcpServerRegistry(
            new Dictionary<string, McpServerDefinition>(StringComparer.Ordinal) { ["roslyn"] = definition },
            provider,
            NullLoggerFactory.Instance,
            TimeProvider.System);
        _disposables.Add(registry);
        registryObserver.Target = registry;
        lateLookup.Registry = registry; // observers run in order: this one runs after the registry stopped the run's servers

        var runId = Guid.NewGuid();
        var created = await provider.CreateAsync(new RunWorkspaceRequest(runId, "sandbox", remote.Url, "main", $"manufacture/{runId}", null), CancellationToken.None);
        created.IsSuccess.Should().BeTrue(created.IsFailure ? created.Error.ToString() : "");
        (await registry.WaitAllReadyAsync(runId, TimeSpan.FromSeconds(30), CancellationToken.None)).IsSuccess.Should().BeTrue();
        int pid;
        await using (var lease = (await registry.GetReadyClientAsync("roslyn", runId, CancellationToken.None)).Value)
        {
            pid = int.Parse(((TextContentBlock)(await lease.Client.CallToolAsync("pid")).Content.Single()).Text, CultureInfo.InvariantCulture);
        }

        var removed = await provider.RemoveAsync(runId, CancellationToken.None);

        lateLookup.Result.Should().NotBeNull("the provider tells every observer before it removes the worktree");
        lateLookup.Result!.Value.IsFailure.Should().BeTrue("the provider no longer reports a workspace whose removal has begun, so the lookup starts nothing");
        IsRunning(pid).Should().BeFalse("the run's server was stopped before the provider removed the worktree");
        removed.IsSuccess.Should().BeTrue(removed.IsFailure ? removed.Error.ToString() : "no server holds the worktree open");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }

        DeleteBestEffort(_root);
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void DeleteBestEffort(string path)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal); // git writes read-only object files
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A worktree's files can stay locked for a moment after git exits on Windows; a stray temp directory is not worth a failure.
        }
    }

    /// <summary>Forwards to the registry, which needs the provider to be built first.</summary>
    private sealed class ForwardingObserver : IRunWorkspaceObserver
    {
        public RunMcpServerRegistry? Target { get; set; }

        public ValueTask OnReadyAsync(RunWorkspace workspace, CancellationToken ct) => Target!.OnReadyAsync(workspace, ct);

        public ValueTask OnRemovingAsync(RunWorkspace workspace, CancellationToken ct) => Target!.OnRemovingAsync(workspace, ct);
    }

    /// <summary>Asks the registry to make the run ready while the provider is removing it: a host gate racing a removal.</summary>
    private sealed class LookupWhenRemovingObserver : IRunWorkspaceObserver
    {
        public RunMcpServerRegistry? Registry { get; set; }

        public UnitResult<AgentError>? Result { get; private set; }

        public ValueTask OnReadyAsync(RunWorkspace workspace, CancellationToken ct) => ValueTask.CompletedTask;

        public async ValueTask OnRemovingAsync(RunWorkspace workspace, CancellationToken ct) =>
            Result = await Registry!.WaitAllReadyAsync(workspace.RunId, TimeSpan.FromSeconds(30), CancellationToken.None);
    }
}
