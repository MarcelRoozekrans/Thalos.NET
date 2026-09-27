using ModelContextProtocol.Client;

namespace Thalos.Mcp;

/// <summary>
/// A shared hold on one run's MCP server client, handed out by <see cref="RunMcpServerRegistry.GetReadyClientAsync"/>.
/// While any lease is held, the registry does not reload or restart that server; dispose the lease as soon as the call
/// made through <see cref="Client"/> returns. Disposing is idempotent. Hold at most one lease per run at a time: a
/// file change marks every server of the run for a reload, so two callers each holding a lease on one of the run's
/// servers while asking for the other would wait for each other forever.
/// </summary>
public sealed class RunMcpClientLease : IAsyncDisposable
{
    private readonly CancellationToken _stopping;
    private Action? _release;

    internal RunMcpClientLease(McpClient client, Action release, CancellationToken stopping)
    {
        Client = client;
        _stopping = stopping;
        _release = release;
    }

    /// <summary>The run's server client; valid until the lease is disposed, or until the run is removed.</summary>
    public McpClient Client { get; }

    /// <summary>
    /// Whether the registry has begun stopping the server, for a removal or a dispose. Set before it disposes the client,
    /// so a call cut off by that dispose sees it; a server that died on its own never sets it.
    /// </summary>
    internal bool ServerStopped => _stopping.IsCancellationRequested;

    /// <summary>Releases the hold, letting a pending reload of the server proceed once no other lease is held.</summary>
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _release, null)?.Invoke();
        return ValueTask.CompletedTask;
    }
}
