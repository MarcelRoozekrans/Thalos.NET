using System.Collections.Concurrent;
using Thalos.Sandbox;
using ZeroAlloc.Results;

namespace Thalos.Tests.Sandbox;

/// <summary>
/// An <see cref="ISandboxRuntime"/> whose every sandbox is a real <see cref="LoopbackSandbox"/> host on loopback Kestrel,
/// started with the spec's run id and token and a work root of its own, so the provider talks to a genuine sandbox host.
/// A test can also seed a handle with no host, mark a sandbox exited, fail a create, or make the list come back empty.
/// </summary>
internal sealed class FakeSandboxRuntime(string root, TimeProvider clock) : ISandboxRuntime, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Entry> _sandboxes = new(StringComparer.Ordinal);
    private int _created;

    /// <summary>Every spec the provider asked to create, in order.</summary>
    public ConcurrentQueue<SandboxSpec> Specs { get; } = new();

    /// <summary>Every sandbox id deleted, in order, including absent ones.</summary>
    public ConcurrentQueue<string> Deleted { get; } = new();

    /// <summary>When set, a create fails with this error and touches nothing.</summary>
    public AgentError? CreateFailure { get; set; }

    /// <summary>When set, the host is started with another token, so it refuses the provider's requests with 401.</summary>
    public bool StartWithWrongToken { get; set; }

    /// <summary>When set, <see cref="ListAsync"/> answers empty, as the runtime does when the engine cannot be asked.</summary>
    public bool ListNothing { get; set; }

    /// <summary>Called once a create's host is running, with the create's token; a test cancels there to interrupt the create.</summary>
    public Action? AfterHostStarted { get; set; }

    /// <summary>The sandboxes that exist.</summary>
    public IReadOnlyCollection<string> Ids => [.. _sandboxes.Keys];

    /// <summary>The work root of a sandbox's host.</summary>
    public string WorkRootOf(string sandboxId) => _sandboxes[sandboxId].WorkRoot;

    /// <summary>A sandbox with no host, as one from before a restart.</summary>
    public void Seed(Guid runId, DateTimeOffset createdAt) =>
        _sandboxes[runId.ToString("N")] = new Entry(null, new SandboxHandle(runId.ToString("N"), runId, SandboxState.Running, new Uri("http://127.0.0.1:9/"), createdAt), "");

    /// <summary>Answers for <paramref name="sandboxId"/> with a handle naming another run, as a confused or hostile engine might.</summary>
    public void Reassign(string sandboxId, Guid otherRun)
    {
        var entry = _sandboxes[sandboxId];
        _sandboxes[sandboxId] = entry with { Handle = entry.Handle with { RunId = otherRun } };
    }

    /// <summary>Marks a sandbox's container exited and stops its host, so its port refuses connections as an exited container's does.</summary>
    public async Task ExitAsync(string sandboxId, int exitCode, bool oomKilled)
    {
        var entry = _sandboxes[sandboxId];
        _sandboxes[sandboxId] = entry with { Handle = entry.Handle with { State = SandboxState.Exited, ExitCode = exitCode, OomKilled = oomKilled } };
        if (entry.Host is { } host)
        {
            await host.StopAsync();
        }
    }

    public async ValueTask<Result<SandboxHandle, AgentError>> CreateAsync(SandboxSpec spec, CancellationToken ct)
    {
        Specs.Enqueue(spec);
        if (CreateFailure is { } failure)
        {
            return Result<SandboxHandle, AgentError>.Failure(failure);
        }

        var workRoot = Path.Combine(root, $"{spec.SandboxId}-{Interlocked.Increment(ref _created)}");
        var token = StartWithWrongToken ? new string('w', 43) : spec.Token;
        var host = await LoopbackSandbox.StartAsync(workRoot, spec.RunId, token);
        var handle = new SandboxHandle(spec.SandboxId, spec.RunId, SandboxState.Running, host.BaseAddress, clock.GetUtcNow());
        _sandboxes[spec.SandboxId] = new Entry(host, handle, workRoot);
        AfterHostStarted?.Invoke();
        ct.ThrowIfCancellationRequested();
        return Result<SandboxHandle, AgentError>.Success(handle);
    }

    public ValueTask<SandboxHandle?> GetAsync(string sandboxId, CancellationToken ct) =>
        ValueTask.FromResult(_sandboxes.TryGetValue(sandboxId, out var entry) ? entry.Handle : null);

    public ValueTask<IReadOnlyList<SandboxHandle>> ListAsync(CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<SandboxHandle>>(ListNothing ? [] : [.. _sandboxes.Values.Select(e => e.Handle)]);

    public async ValueTask<UnitResult<AgentError>> DeleteAsync(string sandboxId, CancellationToken ct)
    {
        Deleted.Enqueue(sandboxId);
        if (_sandboxes.TryRemove(sandboxId, out var entry) && entry.Host is { } host)
        {
            await host.DisposeAsync();
        }

        return UnitResult<AgentError>.Success();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _sandboxes.Values)
        {
            if (entry.Host is { } host)
            {
                await host.DisposeAsync();
            }
        }
    }

    private sealed record Entry(LoopbackSandbox? Host, SandboxHandle Handle, string WorkRoot);
}
