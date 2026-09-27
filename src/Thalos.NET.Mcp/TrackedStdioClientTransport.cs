using System.Runtime.Versioning;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Thalos.Mcp;

/// <summary>
/// A <see cref="StdioClientTransport"/> that remembers the session it connected, when it began, and when the session
/// closed, so that <see cref="RunMcpServerRegistry"/> can later find the process the SDK started and end the rest of its
/// tree with <see cref="ServerProcessTree"/>. It works whether or not a client was ever built on the session: a client
/// whose handshake fails closes the session too.
/// </summary>
/// <remarks>
/// The close is stamped when the SDK completes the session, not when the registry asks: the registry may ask much later,
/// at the next call to a server that died or at the run's removal, and by then the wrapper's process id may have been
/// reused by a process that started children of its own. Those children were created after the close, so the close
/// time keeps them out of the tree; the time of asking would not. The SDK completes the session only after its own
/// wait for the wrapper to exit, which can take up to about twice the server's shutdown timeout, and the stamp then
/// runs on a thread-pool thread. So the stamp can lag the wrapper's exit by seconds, and a process that reuses the id
/// in that window is not kept out. Thalos.NET#192 tracks the root fix, a job object that needs no timestamps.
/// </remarks>
/// <param name="inner">The SDK transport that starts the process.</param>
internal sealed class TrackedStdioClientTransport(StdioClientTransport inner) : IClientTransport
{
    private ITransport? _session;
    private Task<long>? _closedAt;

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
        if (OperatingSystem.IsWindows())
        {
            Volatile.Write(ref _closedAt, StampClose(session));
        }

        Volatile.Write(ref _session, session);
        return session;
    }

    /// <summary>
    /// The time, from <see cref="ServerProcessTree.Now"/>, read once <paramref name="session"/> completes. The SDK's
    /// channel runs continuations asynchronously, so this runs on a thread-pool thread shortly after completion.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static Task<long> StampClose(ITransport session) =>
        session.MessageReader.Completion.ContinueWith(
            static _ => ServerProcessTree.Now(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>
    /// The id of the process the SDK started, as its closed session reports it, and when the session closed, from
    /// <see cref="ServerProcessTree.Now"/>, stamped once the SDK completed it; or <see langword="null"/> when no session was
    /// connected, it has not closed within <paramref name="wait"/>, or it closed without a process id. Windows only.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public async Task<(int Pid, long ClosedAt)?> ClosedProcessAsync(TimeSpan wait)
    {
        if (Volatile.Read(ref _session) is not { } session || Volatile.Read(ref _closedAt) is not { } closed)
        {
            return null;
        }

        long closedAt;
        try
        {
            closedAt = await closed.WaitAsync(wait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null; // still open
        }

        // A stdio session always closes with this exception, carrying the process id.
        return session.MessageReader.Completion.Exception?.InnerException is ClientTransportClosedException { Details: StdioClientCompletionDetails { ProcessId: { } pid } }
            ? (pid, closedAt)
            : null;
    }
}
