using ModelContextProtocol.Client;

namespace Thalos.Mcp;

/// <summary>
/// A shared hold on one run's MCP server client, handed out by <see cref="RunMcpServerRegistry.GetReadyClientAsync"/>.
/// While any lease is held, the registry does not reload or restart that server; dispose the lease as soon as the call
/// made through <see cref="Client"/> returns. Disposing is idempotent.
/// </summary>
public sealed class RunMcpClientLease : IAsyncDisposable
{
    private Action? _release;

    internal RunMcpClientLease(McpClient client, Action release)
    {
        Client = client;
        _release = release;
    }

    /// <summary>The run's server client; valid until the lease is disposed, or until the run is removed.</summary>
    public McpClient Client { get; }

    /// <summary>Releases the hold, letting a pending reload of the server proceed once no other lease is held.</summary>
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _release, null)?.Invoke();
        return ValueTask.CompletedTask;
    }
}
