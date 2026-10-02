using System.Collections.Concurrent;
using Thalos.Sandbox;
using ZeroAlloc.Results;

namespace Thalos.Tests.Sandbox;

/// <summary>
/// An <see cref="ISandboxRuntime"/> whose every sandbox is a real <see cref="LoopbackSandbox"/> host on loopback Kestrel,
/// started with the spec's run id and token and a work root of its own, so the provider talks to a genuine sandbox host.
/// A test can also seed a handle with no host, mark a sandbox exited, fail a create, or make the list come back empty.
/// Like the Docker runtime, <see cref="GetAsync"/> answers <see cref="SandboxState.Missing"/> for a sandbox it does not
/// have, and null only while <see cref="Unreachable"/>. A start of an exited sandbox runs a new host on its work root, as
/// a restarted container runs its host again on its volume, with nothing of the old host's memory.
/// </summary>
internal sealed class FakeSandboxRuntime(string root, TimeProvider clock) : ISandboxRuntime, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Entry> _sandboxes = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<LoopbackSandbox> _stopped = new();
    private int _created;

    /// <summary>Every spec the provider asked to create, in order.</summary>
    public ConcurrentQueue<SandboxSpec> Specs { get; } = new();

    /// <summary>Every sandbox id deleted, in order, including absent ones.</summary>
    public ConcurrentQueue<string> Deleted { get; } = new();

    /// <summary>When set, a create fails with this error and touches nothing.</summary>
    public AgentError? CreateFailure { get; set; }

    /// <summary>How many requests a new sandbox answers 502 first, as the gateway does until a new container's host listens.</summary>
    public int BadGatewayAnswersAfterCreate { get; set; }

    /// <summary>When set, the host is started with another token, so it refuses the provider's requests with 401.</summary>
    public bool StartWithWrongToken { get; set; }

    /// <summary>When set, <see cref="ListAsync"/> answers empty, as the runtime does when the engine cannot be asked.</summary>
    public bool ListNothing { get; set; }

    /// <summary>When set, <see cref="GetAsync"/> answers null for every sandbox, as the runtime does when the engine cannot be asked.</summary>
    public bool Unreachable { get; set; }

    /// <summary>Every sandbox id started again, in order.</summary>
    public ConcurrentQueue<string> Started { get; } = new();

    /// <summary>Called once a start's new host is running; a test cancels there to interrupt the park that started it.</summary>
    public Action? AfterStart { get; set; }

    /// <summary>When set, a start fails with this error and starts nothing.</summary>
    public AgentError? StartFailure { get; set; }

    /// <summary>When set, a delete fails with this error and deletes nothing.</summary>
    public AgentError? DeleteFailure { get; set; }

    /// <summary>Called once a create's host is running, with the create's token; a test cancels there to interrupt the create.</summary>
    public Action? AfterHostStarted { get; set; }

    /// <summary>The sandboxes that exist.</summary>
    public IReadOnlyCollection<string> Ids => [.. _sandboxes.Keys];

    /// <summary>The work root of a sandbox's host.</summary>
    public string WorkRootOf(string sandboxId) => _sandboxes[sandboxId].WorkRoot;

    /// <summary>The host of a sandbox, so a test can change how it answers.</summary>
    public LoopbackSandbox HostOf(string sandboxId) => _sandboxes[sandboxId].Host!;

    /// <summary>Forgets a sandbox and stops its host, as a container deleted behind the runtime's back: it is reported missing.</summary>
    public async Task LoseAsync(string sandboxId)
    {
        if (_sandboxes.TryRemove(sandboxId, out var entry) && entry.Host is { } host)
        {
            await host.StopAsync();
            _stopped.Enqueue(host);
        }
    }

    /// <summary>A sandbox with no host, as one from before a restart.</summary>
    public void Seed(Guid runId, DateTimeOffset createdAt) =>
        _sandboxes[runId.ToString("N")] = new Entry(null, new SandboxHandle(runId.ToString("N"), runId, SandboxState.Running, new Uri("http://127.0.0.1:9/"), createdAt), "", "");

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
        host.BadGatewayAnswers = BadGatewayAnswersAfterCreate;
        var handle = new SandboxHandle(spec.SandboxId, spec.RunId, SandboxState.Running, host.BaseAddress, clock.GetUtcNow());
        _sandboxes[spec.SandboxId] = new Entry(host, handle, workRoot, token);
        AfterHostStarted?.Invoke();
        ct.ThrowIfCancellationRequested();
        return Result<SandboxHandle, AgentError>.Success(handle);
    }

    public ValueTask<SandboxHandle?> GetAsync(string sandboxId, CancellationToken ct)
    {
        if (Unreachable)
        {
            return ValueTask.FromResult<SandboxHandle?>(null);
        }

        return ValueTask.FromResult<SandboxHandle?>(_sandboxes.TryGetValue(sandboxId, out var entry)
            ? entry.Handle
            : new SandboxHandle(sandboxId, Guid.ParseExact(sandboxId, "N"), SandboxState.Missing, new Uri("http://127.0.0.1:9/"), DateTimeOffset.UnixEpoch));
    }

    public async ValueTask<UnitResult<AgentError>> StartAsync(string sandboxId, CancellationToken ct)
    {
        if (StartFailure is { } failure)
        {
            return UnitResult<AgentError>.Failure(failure);
        }

        if (!_sandboxes.TryGetValue(sandboxId, out var entry))
        {
            return UnitResult<AgentError>.Failure(AgentError.ProviderError($"could not start the sandbox '{sandboxId}': it does not exist"));
        }

        if (entry.Handle.State == SandboxState.Running)
        {
            return UnitResult<AgentError>.Success();
        }

        Started.Enqueue(sandboxId);
        if (entry.Host is { } old)
        {
            _stopped.Enqueue(old);
        }

        var host = await LoopbackSandbox.StartAsync(entry.WorkRoot, entry.Handle.RunId, entry.Token);
        _sandboxes[sandboxId] = entry with
        {
            Host = host,
            Handle = entry.Handle with { State = SandboxState.Running, BaseAddress = host.BaseAddress, ExitCode = null, OomKilled = false },
        };
        AfterStart?.Invoke();
        return UnitResult<AgentError>.Success();
    }

    public ValueTask<IReadOnlyList<SandboxHandle>> ListAsync(CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<SandboxHandle>>(ListNothing ? [] : [.. _sandboxes.Values.Select(e => e.Handle)]);

    /// <summary>Sandbox ids whose delete throws, as a runtime with a bug might.</summary>
    public ConcurrentDictionary<string, bool> ThrowOnDelete { get; } = new(StringComparer.Ordinal);

    public async ValueTask<UnitResult<AgentError>> DeleteAsync(string sandboxId, CancellationToken ct)
    {
        if (ThrowOnDelete.ContainsKey(sandboxId))
        {
            throw new InvalidOperationException($"the runtime failed deleting {sandboxId}");
        }

        if (DeleteFailure is { } failure)
        {
            return UnitResult<AgentError>.Failure(failure);
        }

        Deleted.Enqueue(sandboxId);
        if (_sandboxes.TryRemove(sandboxId, out var entry) && entry.Host is { } host)
        {
            await host.DisposeAsync();
        }

        return UnitResult<AgentError>.Success();
    }

    public async ValueTask DisposeAsync()
    {
        while (_stopped.TryDequeue(out var stopped))
        {
            await stopped.DisposeAsync();
        }

        foreach (var entry in _sandboxes.Values)
        {
            if (entry.Host is { } host)
            {
                await host.DisposeAsync();
            }
        }
    }

    private sealed record Entry(LoopbackSandbox? Host, SandboxHandle Handle, string WorkRoot, string Token);
}
