using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Thalos.Mcp;

/// <summary>
/// A <see cref="StdioClientTransport"/> that remembers the session it connected, and when, so that once the session
/// has closed <see cref="RunMcpServerRegistry"/> can find the process the SDK started and end the rest of its tree with
/// <see cref="ServerProcessTree"/>. It works whether or not a client was ever built on the session: a client whose
/// handshake fails closes the session too.
/// </summary>
/// <param name="inner">The SDK transport that starts the process.</param>
internal sealed class TrackedStdioClientTransport(StdioClientTransport inner) : IClientTransport
{
    private ITransport? _session;

    /// <inheritdoc />
    public string Name => inner.Name;

    /// <summary>
    /// On Windows, when the last connect began, from <see cref="ServerProcessTree.Now"/>: every process of the server was
    /// created at or after it. Zero elsewhere.
    /// </summary>
    public long StartedAt { get; private set; }

    /// <inheritdoc />
    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        StartedAt = OperatingSystem.IsWindows() ? ServerProcessTree.Now() : 0;
        var session = await inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _session, session);
        return session;
    }

    /// <summary>
    /// The id of the process the SDK started, as its closed session reports it, or <see langword="null"/> when no session
    /// was connected or it has not closed within <paramref name="wait"/>.
    /// </summary>
    public async Task<int?> ClosedProcessIdAsync(TimeSpan wait)
    {
        if (Volatile.Read(ref _session) is not { } session)
        {
            return null;
        }

        try
        {
            await session.MessageReader.Completion.WaitAsync(wait).ConfigureAwait(false);
        }
        catch (ClientTransportClosedException ex) when (ex.Details is StdioClientCompletionDetails details)
        {
            return details.ProcessId; // a stdio session always closes this way, carrying the process id
        }
        catch (Exception ex) when (ex is TimeoutException or ClientTransportClosedException)
        {
            // Still open, or closed without stdio details.
        }

        return null;
    }
}
