using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Thalos.Runtime;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Mcp;

/// <summary>
/// The tool source for an <c>.mcp.json</c> entry that declares <c>runScoped</c>. Tool schemas come from the host-wide
/// server, the same binary with the same surface. Each call is routed by
/// <see cref="RunWorkspaceClaims.RunIdOf"/> of <see cref="TurnScope.Current"/>'s caller: with no run claim it goes to
/// the host server, as for any MCP source; with one, only to that run's own server, from
/// <see cref="RunMcpServerRegistry"/>. A run caller is never served by the host server or by another run's server.
/// </summary>
/// <remarks>
/// <para>
/// <b>Routing.</b> The key is the turn's caller, which the runtime sets and the model cannot reach; the call's
/// arguments are passed to the chosen server unchanged and play no part in choosing it. A caller whose run claim is
/// present but not a valid id is refused, not treated as a host caller. A run caller whose server is missing, still
/// starting past <see cref="RunScopedMcpDefinition.ReadyWaitTimeout"/>, failed or removed gets an error result
/// starting <c>error: run tool server</c>.
/// </para>
/// <para>
/// <b>Leases.</b> A routed call takes exactly one <see cref="RunMcpClientLease"/>, makes its one call through it and
/// disposes it on every path, including a throw or a cancellation. It never holds a second lease meanwhile, which
/// <see cref="RunMcpServerRegistry"/> requires. The call is bounded by <see cref="RunScopedMcpDefinition.CallTimeout"/>,
/// because a held lease holds back that server's reloads. A removal does not wait for leases: a call whose run's server
/// is stopped under it gets an error result, not a cancellation it never asked for. Only the caller's own token
/// cancelling makes a routed call throw <see cref="OperationCanceledException"/>.
/// </para>
/// <para>
/// <b>Ownership.</b> The source owns <paramref name="host"/> and disposes it; the registry is shared and owned by the
/// container. Only <see cref="IAsyncDisposable"/> is implemented, so a container holding one must be disposed
/// asynchronously.
/// </para>
/// </remarks>
/// <param name="host">The host-wide server's source; it supplies the tool schemas and serves callers without a run.</param>
/// <param name="runScoped">The entry's <c>runScoped</c> section, whose timeouts bound each routed call.</param>
/// <param name="runs">Hands out leases on each run's own server; it must know the server as <paramref name="host"/>'s name.</param>
/// <param name="clock">Times <see cref="RunScopedMcpDefinition.ReadyWaitTimeout"/> and <see cref="RunScopedMcpDefinition.CallTimeout"/>.</param>
/// <param name="logger">Logs refused and timed-out routed calls.</param>
/// <exception cref="ArgumentException">A timeout in <paramref name="runScoped"/> is not positive or is too long for a timer.</exception>
public sealed partial class RunScopedMcpToolSource(
    McpToolSource host, RunScopedMcpDefinition runScoped, RunMcpServerRegistry runs, TimeProvider clock, ILogger<RunScopedMcpToolSource> logger)
    : IToolSource, IAsyncDisposable
{
    private readonly McpToolSource _host = host ?? throw new ArgumentNullException(nameof(host));
    private readonly RunScopedMcpDefinition _runScoped = Validated(host.Name, runScoped);
    private readonly RunMcpServerRegistry _runs = runs ?? throw new ArgumentNullException(nameof(runs));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly ILogger<RunScopedMcpToolSource> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private Routed? _routed;

    /// <inheritdoc />
    public string Name => _host.Name;

    /// <inheritdoc />
    /// <remarks>The host server's tools, each wrapped to route its calls; the wrapped list is built once per host tool list.</remarks>
    /// <exception cref="ObjectDisposedException">The source has been disposed.</exception>
    public async ValueTask<Result<IReadOnlyList<AITool>, AgentError>> GetToolsAsync(CancellationToken ct)
    {
        var tools = await _host.GetClientToolsAsync(ct).ConfigureAwait(false);
        if (tools.IsFailure)
        {
            return Result<IReadOnlyList<AITool>, AgentError>.Failure(tools.Error);
        }

        if (Volatile.Read(ref _routed) is { } routed && ReferenceEquals(routed.HostTools, tools.Value))
        {
            return Result<IReadOnlyList<AITool>, AgentError>.Success(routed.Tools);
        }

        var wrapped = new AITool[tools.Value.Count];
        for (var i = 0; i < wrapped.Length; i++)
        {
            wrapped[i] = new RoutedTool(tools.Value[i], this);
        }

        Volatile.Write(ref _routed, new Routed(tools.Value, wrapped));
        return Result<IReadOnlyList<AITool>, AgentError>.Success(wrapped);
    }

    /// <summary>Disposes the host server's source, shutting its process down. Idempotent.</summary>
    public ValueTask DisposeAsync() => _host.DisposeAsync();

    /// <summary>Calls <paramref name="tool"/> on <paramref name="runId"/>'s own server through one lease, bounded by both timeouts.</summary>
    private async ValueTask<object?> InvokeForRunAsync(McpClientTool tool, Guid runId, AIFunctionArguments arguments, CancellationToken ct)
    {
        Result<RunMcpClientLease, AgentError> lease;
        using (var readyTimeout = new CancellationTokenSource(_runScoped.ReadyWaitTimeout, _clock))
        using (var ready = CancellationTokenSource.CreateLinkedTokenSource(ct, readyTimeout.Token))
        {
            try
            {
                lease = await _runs.GetReadyClientAsync(Name, runId, ready.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (readyTimeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return Refuse(runId, $"its server was not ready within {_runScoped.ReadyWaitTimeout}.");
            }
        }

        if (lease.IsFailure)
        {
            return Refuse(runId, lease.Error.Message);
        }

        await using (lease.Value.ConfigureAwait(false))
        {
            using var callTimeout = new CancellationTokenSource(_runScoped.CallTimeout, _clock);
            using var call = CancellationTokenSource.CreateLinkedTokenSource(ct, callTimeout.Token);
            try
            {
                // The host tool's own definition and serializer options over the run's client: the same arguments reach
                // the server, and the result has the same shape, as a host call's.
                return await new McpClientTool(lease.Value.Client, tool.ProtocolTool, tool.JsonSerializerOptions)
                    .InvokeAsync(arguments, call.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (callTimeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                LogCallTimedOut(_logger, Name, tool.Name, runId, _runScoped.CallTimeout);
                return $"error: run tool server '{Name}' did not answer '{tool.Name}' within {_runScoped.CallTimeout} for this run; the call was cancelled.";
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // Neither the caller nor the timeout: the SDK cancels a call in flight when its session closes, which is
                // how a removal, which does not wait for leases, stops the run's server under it. Reported, not thrown as
                // a cancellation the caller never asked for.
                LogCallCutOff(_logger, ex, Name, tool.Name, runId);
                return $"error: run tool server '{Name}' stopped during '{tool.Name}' for this run; the call did not complete.";
            }
        }
    }

    private string Refuse(Guid? runId, string reason)
    {
        LogRefused(_logger, Name, runId, reason);
        return $"error: run tool server '{Name}' is not available for this run: {reason}";
    }

    private static RunScopedMcpDefinition Validated(string name, RunScopedMcpDefinition runScoped)
    {
        ArgumentNullException.ThrowIfNull(runScoped);
        RunMcpServerRegistry.ThrowIfInvalidTimeouts(name, runScoped, nameof(runScoped));
        return runScoped;
    }

    /// <summary>The wrapped tools and the host list they were built from.</summary>
    private sealed record Routed(IReadOnlyList<McpClientTool> HostTools, AITool[] Tools);

    /// <summary>A host tool whose calls go to the calling run's own server when the turn's caller carries a run claim.</summary>
    private sealed class RoutedTool(McpClientTool hostTool, RunScopedMcpToolSource source) : DelegatingAIFunction(hostTool)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            if (TurnScope.Current?.Caller is not { } caller || !caller.Claims.ContainsKey(RunWorkspaceClaims.RunId))
            {
                return await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false); // no run: the host server, as before
            }

            return RunWorkspaceClaims.RunIdOf(caller) is { } runId
                ? await source.InvokeForRunAsync(hostTool, runId, arguments, cancellationToken).ConfigureAwait(false)
                : source.Refuse(runId: null, "the caller's run claim is not a valid run id.");
        }
    }

    [LoggerMessage(EventId = 330, Level = LogLevel.Warning, Message = "Refused a call to run tool server '{Server}' for run {RunId}: {Reason}")]
    private static partial void LogRefused(ILogger logger, string server, Guid? runId, string reason);

    [LoggerMessage(EventId = 331, Level = LogLevel.Warning, Message = "Call to '{Tool}' on run tool server '{Server}' for run {RunId} did not finish within {Timeout} and was cancelled")]
    private static partial void LogCallTimedOut(ILogger logger, string server, string tool, Guid runId, TimeSpan timeout);

    [LoggerMessage(EventId = 332, Level = LogLevel.Warning, Message = "Call to '{Tool}' on run tool server '{Server}' for run {RunId} was cut off: the server's session closed")]
    private static partial void LogCallCutOff(ILogger logger, Exception exception, string server, string tool, Guid runId);
}
