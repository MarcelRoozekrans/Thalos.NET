using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Thalos.Mcp;
using ZeroAlloc.Results;

namespace Thalos.Sandbox.Host;

/// <summary>Where the run's Roslyn server stands.</summary>
internal enum RoslynState
{
    /// <summary>Not started, or still loading the solution.</summary>
    Pending,

    /// <summary>Answering its ready tool.</summary>
    Ready,

    /// <summary>The import failed, or the server did not become ready.</summary>
    Failed,
}

/// <summary>
/// The <c>/mcp/roslyn</c> route's tools: the local RoslynCodeLens stdio server's own tools, proxied one for one. The
/// server is the single <c>roslyn</c> entry of a <see cref="RunMcpServerRegistry"/> over <see cref="LocalRunWorkspace"/>,
/// so it starts once the workspace is ready and reloads after workspace writes, as a run-scoped server does on a
/// host.
/// </summary>
/// <remarks>
/// The tool list is read once, through a lease, the first time it is asked for after the server is ready, and kept:
/// RoslynCodeLens offers the same tools whatever it has loaded. Before the server is ready the route offers no tools.
/// Every call first awaits <see cref="RestoreService.EnsureRestoredAsync"/>, then takes a lease, which applies a
/// pending reload, and forwards the call unchanged. As on a host, the wait for the lease and the call are bounded by
/// <see cref="ReadyWaitTimeout"/> and <see cref="CallTimeout"/>; either answers with an error result.
/// </remarks>
/// <param name="settings">The run id.</param>
/// <param name="registry">Owns the Roslyn server process.</param>
/// <param name="restore">Restores again after a build-file change, before the call.</param>
/// <param name="logger">Logs the server's readiness.</param>
internal sealed partial class RoslynProxyTools(SandboxSettings settings, RunMcpServerRegistry registry, RestoreService restore, ILogger<RoslynProxyTools> logger)
{
    /// <summary>The registry entry's name.</summary>
    public const string ServerName = "roslyn";

    /// <summary>How long the import waits for the server to load the solution and answer its ready tool.</summary>
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(15);

    private readonly object _sync = new();
    private RoslynState _state;
    private string? _detail;
    private IReadOnlyList<Tool>? _tools;

    /// <summary>The server's state and, when it failed, why.</summary>
    public (RoslynState State, string? Detail) Snapshot
    {
        get
        {
            lock (_sync)
            {
                return (_state, _detail);
            }
        }
    }

    /// <summary>
    /// The Roslyn server's definition. The child inherits none of the host's environment beyond the SDK's curated
    /// defaults and the names listed here: never <see cref="SandboxEnvironment.Token"/>, because the server evaluates
    /// the run's MSBuild files, which an agent may have written.
    /// </summary>
    /// <param name="settings">The command, its arguments and the reload setting.</param>
    public static McpServerDefinition Definition(SandboxSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new McpServerDefinition
        {
            Type = "stdio",
            Command = settings.RoslynCommand,
            Timeout = TimeSpan.FromMinutes(2),
            PassEnvironment = ["HOME", "DOTNET_CLI_HOME", "DOTNET_ROOT", "DOTNET_NOLOGO", "DOTNET_CLI_TELEMETRY_OPTOUT", "NUGET_PACKAGES"],
            RunScoped = new RunScopedMcpDefinition
            {
                Args = [.. settings.RoslynArgs, "${run.workspace.solution}"],
                Env = new Dictionary<string, string>(StringComparer.Ordinal) { ["ROSLYN_CODELENS_OPEN_PROJECT_TIMEOUT_SECONDS"] = "600" },
                ReadyTool = "list_solutions",
                Reload = settings.RoslynReload,
            },
        };
    }

    /// <summary>Waits until the server is ready, records the outcome, and returns it. The import calls it once, after the workspace was published ready.</summary>
    /// <param name="ct">Stops the wait, not the server.</param>
    public async Task WaitReadyAsync(CancellationToken ct)
    {
        var ready = await registry.WaitAllReadyAsync(settings.RunId, ReadyTimeout, ct).ConfigureAwait(false);
        if (ready.IsSuccess)
        {
            Set(RoslynState.Ready, null);
            LogReady(logger);
        }
        else
        {
            Set(RoslynState.Failed, ready.Error.Message);
            LogNotReady(logger, ready.Error.Message);
        }
    }

    /// <summary>Records that the server will never start, because the import failed.</summary>
    /// <param name="detail">Why.</param>
    public void Fail(string detail) => Set(RoslynState.Failed, detail);

    /// <summary>The route's tools: none before the server is ready, the server's own afterwards.</summary>
    /// <param name="caller">The caller each call runs as.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask<IReadOnlyList<McpServerTool>> GetToolsAsync(SandboxCaller caller, CancellationToken ct)
    {
        var listed = Volatile.Read(ref _tools);
        if (listed is null)
        {
            if (Snapshot.State != RoslynState.Ready)
            {
                return [];
            }

            var lease = await registry.GetReadyClientAsync(ServerName, settings.RunId, ct).ConfigureAwait(false);
            if (lease.IsFailure)
            {
                return [];
            }

            await using (lease.Value.ConfigureAwait(false))
            {
                var tools = await lease.Value.Client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
                listed = [.. tools.Select(t => t.ProtocolTool)];
            }

            Volatile.Write(ref _tools, listed);
        }

        return [.. listed.Select(tool => (McpServerTool)new SandboxScopedTool(new ProxyTool(this, tool), caller, restore.EnsureRestoredAsync))];
    }

    /// <summary>How long a call waits for the server to take it: a start or reload in progress. The definition's <see cref="RunScopedMcpDefinition.ReadyWaitTimeout"/>.</summary>
    internal TimeSpan ReadyWaitTimeout { get; set; } = new RunScopedMcpDefinition().ReadyWaitTimeout;

    /// <summary>
    /// How long one call may run. A call holds the server's lease, and a reload waits for every lease, so an unbounded
    /// call would hold reloads back. The definition's <see cref="RunScopedMcpDefinition.CallTimeout"/>.
    /// </summary>
    internal TimeSpan CallTimeout { get; set; } = new RunScopedMcpDefinition().CallTimeout;

    private async ValueTask<CallToolResult> CallAsync(string name, IDictionary<string, System.Text.Json.JsonElement>? arguments, CancellationToken ct)
    {
        Result<RunMcpClientLease, AgentError> lease;
        using (var readyTimeout = new CancellationTokenSource(ReadyWaitTimeout))
        using (var ready = CancellationTokenSource.CreateLinkedTokenSource(ct, readyTimeout.Token))
        {
            try
            {
                lease = await registry.GetReadyClientAsync(ServerName, settings.RunId, ready.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (readyTimeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return Error($"the Roslyn server was not ready within {ReadyWaitTimeout}.");
            }
        }

        if (lease.IsFailure)
        {
            return Error(lease.Error.Message);
        }

        await using (lease.Value.ConfigureAwait(false))
        {
            using var callTimeout = new CancellationTokenSource(CallTimeout);
            using var call = CancellationTokenSource.CreateLinkedTokenSource(ct, callTimeout.Token);
            try
            {
                return await lease.Value.Client.CallToolAsync(new CallToolRequestParams { Name = name, Arguments = arguments }, call.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (callTimeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                LogCallTimedOut(logger, name, CallTimeout);
                return Error($"the Roslyn server did not answer '{name}' within {CallTimeout}; the call was cancelled.");
            }
        }
    }

    private static CallToolResult Error(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = $"error: {message}" }] };

    private void Set(RoslynState state, string? detail)
    {
        lock (_sync)
        {
            _state = state;
            _detail = detail;
        }
    }

    [LoggerMessage(EventId = 5913, Level = LogLevel.Information, Message = "The Roslyn server is ready.")]
    private static partial void LogReady(ILogger logger);

    [LoggerMessage(EventId = 5917, Level = LogLevel.Warning, Message = "The Roslyn call {Tool} did not finish within {Timeout} and was cancelled.")]
    private static partial void LogCallTimedOut(ILogger logger, string tool, TimeSpan timeout);

    [LoggerMessage(EventId = 5914, Level = LogLevel.Warning, Message = "The Roslyn server did not become ready: {Detail}")]
    private static partial void LogNotReady(ILogger logger, string detail);

    /// <summary>One of the server's tools, under its own name and schema, forwarded through a lease.</summary>
    private sealed class ProxyTool(RoslynProxyTools owner, Tool tool) : McpServerTool
    {
        public override Tool ProtocolTool => tool;

        public override IReadOnlyList<object> Metadata => [];

        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default) =>
            owner.CallAsync(tool.Name, request?.Params?.Arguments, cancellationToken);
    }
}
