using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Thalos.Runtime;
using Thalos.Workspaces;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos.Mcp;

/// <summary>
/// A tool source whose calls are served by each run's own remote tool endpoint, such as the run's sandbox, which
/// <see cref="IRunToolEndpointResolver"/> finds per call. Each call is routed by <see cref="RunWorkspaceClaims.RunIdOf"/>
/// of <see cref="TurnScope.Current"/>'s caller, never by anything in the call's arguments: a run caller is served only by
/// its own run's endpoint, never by another run's and never by the host.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two kinds.</b> <see cref="ForMcpHost"/> takes the tool schemas from a host-wide MCP server, which also serves a
/// caller with no run claim, as <see cref="RunScopedMcpToolSource"/> does. <see cref="ForLocalSchemas"/> takes them from
/// a local tool source that is never invoked through this source; a caller with no run claim is refused with
/// <c>error: '&lt;name&gt;' tools are only available inside a workflow run.</c> A caller whose run claim is present but
/// not a valid id is refused in both kinds.
/// </para>
/// <para>
/// <b>Clients.</b> One MCP client per run, over Streamable HTTP with the endpoint's bearer token in the
/// <c>Authorization</c> header, is cached by run id. A client is replaced when the resolver returns a different endpoint
/// for the run, when its session has ended, or when a call finds its transport closed or its answer unreadable.
/// <see cref="OnRemovingAsync"/> drops and disposes the run's client and refuses the run until <see cref="OnReadyAsync"/>
/// is told it is ready again; a client a racing call makes after a removal or after <see cref="DisposeAsync"/> is
/// disposed by that call. The token is never logged.
/// </para>
/// <para>
/// <b>Bounds.</b> Resolving and connecting are bounded by <see cref="RemoteRunToolOptions.ConnectTimeout"/>, a call by
/// <see cref="RemoteRunToolOptions.CallTimeout"/>. A run with no endpoint, a resolver failure, a failed connection, a
/// timeout, a closed session, a failed request or an answer the client cannot read gives an <c>error:</c> result, never
/// an exception: the endpoint runs agent-controlled code, so its answers are untrusted. Only the caller's own token
/// cancelling makes a call throw <see cref="OperationCanceledException"/>.
/// </para>
/// <para>
/// <b>Observers.</b> Each <see cref="IRunToolCallObserver"/> is told of every call that was sent to a run's endpoint,
/// including one answered with an <c>error:</c> result, but not of a refusal. An observer's exception is logged and never
/// changes the result, and the call waits at most <see cref="RemoteRunToolOptions.ObserverTimeout"/> for each observer.
/// </para>
/// <para>
/// <b>Ownership.</b> The source owns the host source or the schema source it was made from, and disposes it if it is
/// disposable, along with every cached client. <see cref="DisposeAsync"/> is idempotent, so a container that registers
/// one instance under several service types can dispose it more than once.
/// </para>
/// </remarks>
public sealed partial class RemoteRunToolSource : IToolSource, IRunWorkspaceObserver, IAsyncDisposable
{
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>How many removed runs are remembered, so a client a racing call caches after the removal is dropped.</summary>
    private const int MaxRemovedRuns = 4096;

    /// <summary>The reply for a run that has no sandbox, or whose sandbox was removed.</summary>
    private const string NoSandbox = "the run has no sandbox (it may be parked or removed).";

    private readonly McpToolSource? _host;
    private readonly IToolSource? _schemas;
    private readonly IRunToolEndpointResolver _endpoints;
    private readonly RemoteRunToolOptions _options;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<RemoteRunToolSource> _logger;
    private readonly TimeProvider _clock;
    private readonly IEnumerable<IRunToolCallObserver> _observers;
    private readonly ConcurrentDictionary<Guid, CachedClient> _clients = new();
    private readonly ConcurrentDictionary<Guid, long> _removed = new();
    private readonly CancellationTokenSource _disposing = new();
    private Routed? _routed;
    private int _disposed;

    private RemoteRunToolSource(
        string name,
        McpToolSource? host,
        IToolSource? schemas,
        IRunToolEndpointResolver endpoints,
        RemoteRunToolOptions options,
        ILoggerFactory loggers,
        TimeProvider clock,
        IEnumerable<IRunToolCallObserver>? callObservers)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggers);
        ArgumentNullException.ThrowIfNull(clock);
        ThrowIfInvalid(
            name,
            nameof(options),
            (nameof(options.ConnectTimeout), options.ConnectTimeout),
            (nameof(options.CallTimeout), options.CallTimeout),
            (nameof(options.ObserverTimeout), options.ObserverTimeout));
        Name = name;
        _host = host;
        _schemas = schemas;
        _endpoints = endpoints;
        _options = new RemoteRunToolOptions { ConnectTimeout = options.ConnectTimeout, CallTimeout = options.CallTimeout, ObserverTimeout = options.ObserverTimeout };
        _loggers = loggers;
        _logger = loggers.CreateLogger<RemoteRunToolSource>();
        _clock = clock;
        _observers = callObservers ?? [];
    }

    /// <summary>
    /// A source whose schemas come from <paramref name="host"/>, a host-wide MCP server, which also serves every caller
    /// with no run claim, as <see cref="RunScopedMcpToolSource"/> does. A run caller is served by its run's endpoint.
    /// </summary>
    /// <param name="host">The host-wide server's source, owned and disposed by the new source; its name is the source's name.</param>
    /// <param name="endpoints">Finds a run's endpoint for each call.</param>
    /// <param name="options">The connect and call timeouts, copied.</param>
    /// <param name="loggers">For the source's own log and the MCP clients'.</param>
    /// <param name="clock">Times both timeouts and each call's elapsed time.</param>
    /// <param name="callObservers">Told of each completed run call; enumerated per call, so it may be resolved lazily.</param>
    /// <exception cref="ArgumentException">A timeout in <paramref name="options"/> is not positive or is too long for a timer.</exception>
    public static RemoteRunToolSource ForMcpHost(
        McpToolSource host,
        IRunToolEndpointResolver endpoints,
        RemoteRunToolOptions options,
        ILoggerFactory loggers,
        TimeProvider clock,
        IEnumerable<IRunToolCallObserver>? callObservers = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new RemoteRunToolSource(host.Name, host, schemas: null, endpoints, options, loggers, clock, callObservers);
    }

    /// <summary>
    /// A source whose schemas come from <paramref name="schemaSource"/>, which is never invoked through it: a caller with
    /// no run claim is refused, and a run caller is served by its run's endpoint, which must offer the same tools under
    /// the same names.
    /// </summary>
    /// <param name="name">The source's name; the tools are exposed as <c>{name}__{tool}</c>.</param>
    /// <param name="schemaSource">Supplies the tool schemas; owned and disposed, if disposable, by the new source.</param>
    /// <param name="endpoints">Finds a run's endpoint for each call.</param>
    /// <param name="options">The connect and call timeouts, copied.</param>
    /// <param name="loggers">For the source's own log and the MCP clients'.</param>
    /// <param name="clock">Times both timeouts and each call's elapsed time.</param>
    /// <param name="callObservers">Told of each completed run call; enumerated per call, so it may be resolved lazily.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> violates <see cref="ToolSourceName"/>, or a timeout in <paramref name="options"/> is not
    /// positive or is too long for a timer.
    /// </exception>
    public static RemoteRunToolSource ForLocalSchemas(
        string name,
        IToolSource schemaSource,
        IRunToolEndpointResolver endpoints,
        RemoteRunToolOptions options,
        ILoggerFactory loggers,
        TimeProvider clock,
        IEnumerable<IRunToolCallObserver>? callObservers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ToolSourceName.ThrowIfInvalid(name, nameof(name));
        ArgumentNullException.ThrowIfNull(schemaSource);
        return new RemoteRunToolSource(name, host: null, schemaSource, endpoints, options, loggers, clock, callObservers);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>The local schema source, for tests that prove its functions cannot act; null for an MCP-host source.</summary>
    internal IToolSource? SchemaSource => _schemas;

    /// <inheritdoc />
    /// <remarks>The schema tools, each wrapped to route its calls; the wrapped list is built once per schema list.</remarks>
    /// <exception cref="ObjectDisposedException">The source has been disposed.</exception>
    public async ValueTask<Result<IReadOnlyList<AITool>, AgentError>> GetToolsAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        object schemaList;
        IReadOnlyList<AIFunction> functions;
        if (_host is not null)
        {
            var tools = await _host.GetClientToolsAsync(ct).ConfigureAwait(false);
            if (tools.IsFailure)
            {
                return Result<IReadOnlyList<AITool>, AgentError>.Failure(tools.Error);
            }

            schemaList = tools.Value;
            functions = tools.Value;
        }
        else
        {
            var tools = await _schemas!.GetToolsAsync(ct).ConfigureAwait(false);
            if (tools.IsFailure)
            {
                return Result<IReadOnlyList<AITool>, AgentError>.Failure(tools.Error);
            }

            schemaList = tools.Value;
            functions = [.. tools.Value.OfType<AIFunction>()];
        }

        if (Volatile.Read(ref _routed) is { } routed && ReferenceEquals(routed.Schemas, schemaList))
        {
            return Result<IReadOnlyList<AITool>, AgentError>.Success(routed.Tools);
        }

        var wrapped = new AITool[functions.Count];
        for (var i = 0; i < wrapped.Length; i++)
        {
            wrapped[i] = new RoutedRemoteTool(functions[i], this);
        }

        Volatile.Write(ref _routed, new Routed(schemaList, wrapped));
        return Result<IReadOnlyList<AITool>, AgentError>.Success(wrapped);
    }

    /// <summary>Forgets that the run was removed, if it was, so a run made ready again is served again. A run's client is created on its first call.</summary>
    public ValueTask OnReadyAsync(RunWorkspace workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _removed.TryRemove(workspace.RunId, out _);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Drops and disposes the run's cached client, if any, and remembers the run as removed until it is made ready again.
    /// A call that resolved the run's endpoint just before the removal therefore drops the client it gets afterwards
    /// rather than keeping it. A repeat finds no client and does nothing more.
    /// </summary>
    public async ValueTask OnRemovingAsync(RunWorkspace workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        // Remembered before the client is taken out: a call that gets a client after this point finds the run removed.
        _removed[workspace.RunId] = _clock.GetTimestamp();
        TrimRemoved();
        if (_clients.TryRemove(workspace.RunId, out var cached))
        {
            await DisposeClientAsync(workspace.RunId, cached).ConfigureAwait(false);
        }
    }

    /// <summary>Disposes every cached client and the owned host or schema source. Idempotent.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _disposing.CancelAsync().ConfigureAwait(false);
        foreach (var runId in _clients.Keys)
        {
            if (_clients.TryRemove(runId, out var cached))
            {
                await DisposeClientAsync(runId, cached).ConfigureAwait(false);
            }
        }

        if (_host is not null)
        {
            await _host.DisposeAsync().ConfigureAwait(false);
        }
        else if (_schemas is IAsyncDisposable asyncSchemas)
        {
            await asyncSchemas.DisposeAsync().ConfigureAwait(false);
        }
        else if (_schemas is IDisposable schemas)
        {
            schemas.Dispose();
        }

        _disposing.Dispose();
    }

    /// <summary>Resolves the run's endpoint, calls the tool there, tells the observers, and returns the result.</summary>
    private async ValueTask<object?> InvokeForRunAsync(RoutedRemoteTool tool, Guid runId, ISecurityContext caller, AIFunctionArguments arguments, CancellationToken ct)
    {
        var started = _clock.GetTimestamp();
        RunToolEndpoint? endpoint;
        using (var resolveTimeout = new CancellationTokenSource(_options.ConnectTimeout, _clock))
        using (var resolve = CancellationTokenSource.CreateLinkedTokenSource(ct, resolveTimeout.Token))
        {
            try
            {
                endpoint = await _endpoints.ResolveAsync(runId, Name, resolve.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (resolveTimeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return Refuse(runId, $"its endpoint was not resolved within {_options.ConnectTimeout}.");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // The resolver's failure is the host's, but it must not reach the agent as an exception.
                LogResolveFailed(_logger, ex, Name, runId, ex.GetType().Name);
                return Refuse(runId, "its endpoint could not be resolved.");
            }
        }

        if (endpoint is null)
        {
            return Refuse(runId, NoSandbox);
        }

        var connected = await ClientForAsync(runId, endpoint, ct).ConfigureAwait(false);
        switch (connected.State)
        {
            case ClientState.Removed:
                return Refuse(runId, NoSandbox);
            case ClientState.Disposed:
                return Refuse(runId, "the tool source has been shut down.");
        }

        var result = connected.State == ClientState.Connected
            ? await CallAsync(tool, runId, connected.Client!, connected.Entry!, arguments, ct).ConfigureAwait(false)
            : $"error: the run's sandbox did not answer '{tool.Name}': no connection could be made to it.";
        await NotifyAsync(new RunToolCall(runId, Name, tool.Name, caller, TextOf(result), _clock.GetElapsedTime(started)), ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>Makes the one call through the run's client, bounded by <see cref="RemoteRunToolOptions.CallTimeout"/>.</summary>
    private async ValueTask<object?> CallAsync(RoutedRemoteTool tool, Guid runId, McpClient client, CachedClient entry, AIFunctionArguments arguments, CancellationToken ct)
    {
        using var callTimeout = new CancellationTokenSource(_options.CallTimeout, _clock);
        using var call = CancellationTokenSource.CreateLinkedTokenSource(ct, callTimeout.Token);
        try
        {
            return tool.HostTool is { } hostTool
                ? await CallUntilSessionEndsAsync(new McpClientTool(client, hostTool.ProtocolTool, hostTool.JsonSerializerOptions).InvokeAsync(arguments, call.Token).AsTask(), client, call).ConfigureAwait(false)
                : TextResult(tool.Name, await CallUntilSessionEndsAsync(client.CallToolAsync(tool.Name, new Dictionary<string, object?>(arguments, StringComparer.Ordinal), cancellationToken: call.Token).AsTask(), client, call).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (callTimeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            LogCallTimedOut(_logger, Name, tool.Name, runId, _options.CallTimeout);
            return $"error: the run's sandbox did not answer '{tool.Name}' within {_options.CallTimeout}; the call was cancelled.";
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Neither the caller nor the timeout: the client's session ended under the call.
            return await StoppedDuringAsync(tool, runId, entry, ex).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is not OperationCanceledException && client.Completion.IsCompleted))
        {
            return await StoppedDuringAsync(tool, runId, entry, ex).ConfigureAwait(false);
        }
        catch (McpException ex)
        {
            // The endpoint answered with a protocol error, such as an unknown tool: its own answer, as text.
            LogCallRefused(_logger, Name, tool.Name, runId, LogSanitizer.Clean(ex.Message));
            return $"error: the run's sandbox refused '{tool.Name}': {ex.Message}";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The sandbox runs agent-controlled code, so its answer is untrusted: one the client cannot read in any way is
            // an error result, and the client that read it is not reused.
            // Logged as its type and a cleaned message only: the message may quote what the sandbox sent.
            LogUnusableAnswer(_logger, Name, tool.Name, runId, ex.GetType().Name, LogSanitizer.Clean(ex.Message));
            await DropAsync(runId, entry).ConfigureAwait(false);
            return $"error: the run's sandbox gave no usable answer to '{tool.Name}'; the call did not complete.";
        }
    }

    /// <summary>
    /// Awaits <paramref name="invocation"/>, cancelling <paramref name="call"/> if <paramref name="client"/>'s session ends
    /// first, as <see cref="RunScopedMcpToolSource"/> does: a request sent while the session closes is never answered.
    /// </summary>
    private static async Task<T> CallUntilSessionEndsAsync<T>(Task<T> invocation, McpClient client, CancellationTokenSource call)
    {
        if (await Task.WhenAny(invocation, client.Completion).ConfigureAwait(false) != invocation)
        {
            await call.CancelAsync().ConfigureAwait(false);
        }

        return await invocation.ConfigureAwait(false);
    }

    /// <summary>The first text content of a local-schema call's result, or an <c>error:</c> result when it is an error.</summary>
    private static string TextResult(string tool, CallToolResult result)
    {
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? string.Empty;
        if (result.IsError != true)
        {
            return text;
        }

        return text.StartsWith("error:", StringComparison.Ordinal) ? text
            : text.Length == 0 ? $"error: the run's sandbox failed '{tool}'."
            : $"error: {text}";
    }

    /// <summary>
    /// The run's cached client for <paramref name="endpoint"/>, created on first use. A client cached for another
    /// endpoint, or whose session has ended, is dropped and created again, once. A failure to connect is logged, without
    /// the token. A client got for a run that was removed meanwhile, or after the source was disposed, is dropped and
    /// disposed here, so neither a removal nor the disposal can miss it.
    /// </summary>
    private async ValueTask<Connection> ClientForAsync(Guid runId, RunToolEndpoint endpoint, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (Gone(runId) is { } gone)
            {
                return new Connection(gone, null, null);
            }

            var entry = _clients.GetOrAdd(runId, static (id, state) => state.Source.NewClient(id, state.Endpoint), (Source: this, Endpoint: endpoint));
            if (!entry.Endpoint.Equals(endpoint))
            {
                await DropAsync(runId, entry).ConfigureAwait(false);
                continue;
            }

            McpClient client;
            try
            {
                client = await entry.Client.Value.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogConnectFailed(_logger, ex, Name, runId, ex.GetType().Name);
                _clients.TryRemove(KeyValuePair.Create(runId, entry));
                return new Connection(ClientState.Failed, null, null);
            }

            // Checked again once the client exists: a removal or disposal that came before this point either took the
            // entry out after the client was made, and disposed it, or is seen here.
            if (Gone(runId) is { } goneSince)
            {
                _clients.TryRemove(KeyValuePair.Create(runId, entry));
                await DisposeQuietlyAsync(runId, client).ConfigureAwait(false);
                return new Connection(goneSince, null, null);
            }

            if (client.Completion.IsCompleted)
            {
                await DropAsync(runId, entry).ConfigureAwait(false);
                continue;
            }

            return new Connection(ClientState.Connected, client, entry);
        }

        return new Connection(ClientState.Failed, null, null);
    }

    /// <summary>Why no client may be used for the run now, or null when one may.</summary>
    private ClientState? Gone(Guid runId) =>
        Volatile.Read(ref _disposed) != 0 ? ClientState.Disposed
        : _removed.ContainsKey(runId) ? ClientState.Removed
        : null;

    /// <summary>Keeps at most <see cref="MaxRemovedRuns"/> removed runs, forgetting the longest-removed first.</summary>
    private void TrimRemoved()
    {
        while (_removed.Count > MaxRemovedRuns)
        {
            _removed.TryRemove(_removed.MinBy(kv => kv.Value));
        }
    }

    private CachedClient NewClient(Guid runId, RunToolEndpoint endpoint) =>
        new(endpoint, new Lazy<Task<McpClient>>(() => ConnectAsync(runId, endpoint), LazyThreadSafetyMode.ExecutionAndPublication));

    /// <summary>Connects to <paramref name="endpoint"/>, bounded by the connect timeout and the source's disposal, never by one caller's token.</summary>
    private async Task<McpClient> ConnectAsync(Guid runId, RunToolEndpoint endpoint)
    {
        using var timeout = new CancellationTokenSource(_options.ConnectTimeout, _clock);
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _disposing.Token);
        LogConnecting(_logger, Name, runId, endpoint.Endpoint);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Name = $"{Name}-{runId:N}",
                Endpoint = endpoint.Endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>(StringComparer.Ordinal) { ["Authorization"] = $"Bearer {endpoint.BearerToken}" },
                ConnectionTimeout = _options.ConnectTimeout,

                // The sandbox serves MCP statelessly; nothing is pushed outside a call's own response.
                EnableStandaloneGetStream = false,
            },
            _loggers);
        return await McpClient.CreateAsync(transport, clientOptions: null, _loggers, connect.Token).ConfigureAwait(false);
    }

    private async ValueTask<string> StoppedDuringAsync(RoutedRemoteTool tool, Guid runId, CachedClient entry, Exception ex)
    {
        LogCallCutOff(_logger, ex, Name, tool.Name, runId);
        await DropAsync(runId, entry).ConfigureAwait(false);
        return $"error: the run's sandbox stopped during '{tool.Name}'; the call did not complete.";
    }

    /// <summary>Removes <paramref name="entry"/> if it is still the run's client, and disposes it.</summary>
    private async ValueTask DropAsync(Guid runId, CachedClient entry)
    {
        if (_clients.TryRemove(KeyValuePair.Create(runId, entry)))
        {
            await DisposeClientAsync(runId, entry).ConfigureAwait(false);
        }
    }

    /// <summary>Disposes the entry's client once its connection attempt, which is bounded by the connect timeout, has finished.</summary>
    private async ValueTask DisposeClientAsync(Guid runId, CachedClient entry)
    {
        if (!entry.Client.IsValueCreated)
        {
            return;
        }

        McpClient client;
        try
        {
            client = await entry.Client.Value.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return; // a failed connection has nothing to dispose; the call that made it logged why
        }

        await DisposeQuietlyAsync(runId, client).ConfigureAwait(false);
    }

    private async ValueTask DisposeQuietlyAsync(Guid runId, McpClient client)
    {
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
            LogClientDisposed(_logger, Name, runId);
        }
        catch (Exception ex)
        {
            LogDisposeFailed(_logger, ex, Name, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Tells each observer of <paramref name="call"/>, waiting at most <see cref="RemoteRunToolOptions.ObserverTimeout"/>
    /// for each. An observer that takes longer has its token cancelled and is left to finish on its own; the call goes on.
    /// </summary>
    private async ValueTask NotifyAsync(RunToolCall call, CancellationToken ct)
    {
        foreach (var observer in _observers)
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                await observer.OnCompletedAsync(call, bounded.Token).AsTask().WaitAsync(_options.ObserverTimeout, _clock, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException)
            {
                // The one bound: the observer is told to stop here, through its token, and left to finish on its own.
                await bounded.CancelAsync().ConfigureAwait(false);
                LogObserverTimedOut(_logger, Name, call.Tool, call.RunId, _options.ObserverTimeout);
            }
            catch (Exception ex)
            {
                LogObserverFailed(_logger, ex, Name, call.Tool, call.RunId);
            }
        }
    }

    /// <summary>
    /// The text of a call's result: a string as it is; the text of the content an MCP-host tool returns, a
    /// <see cref="TextContent"/> or a list of contents; an MCP call result's text content; otherwise its JSON or text.
    /// </summary>
    private static string TextOf(object? result) => result switch
    {
        null => string.Empty,
        string text => text,
        TextContent content => content.Text,
        IEnumerable<AIContent> contents => string.Concat(contents.OfType<TextContent>().Select(c => c.Text)),
        JsonElement { ValueKind: JsonValueKind.String } text => text.GetString() ?? string.Empty,
        JsonElement { ValueKind: JsonValueKind.Object } json when json.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array =>
            string.Concat(content.EnumerateArray()
                .Where(block => block.ValueKind == JsonValueKind.Object && block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                .Select(block => block.GetProperty("text").GetString())),
        JsonElement json => json.GetRawText(),
        _ => result.ToString() ?? string.Empty,
    };

    private string Refuse(Guid? runId, string reason)
    {
        LogRefused(_logger, Name, runId, reason);
        return $"error: run tool server '{Name}' is not available for this run: {reason}";
    }

    private string RefuseNoRun()
    {
        LogRefused(_logger, Name, runId: null, "the caller has no run.");
        return $"error: '{Name}' tools are only available inside a workflow run.";
    }

    private static void ThrowIfInvalid(string name, string paramName, params (string Property, TimeSpan Value)[] timeouts)
    {
        foreach (var (property, value) in timeouts)
        {
            if (value <= TimeSpan.Zero || value > MaxTimeout)
            {
                throw new ArgumentException($"Remote run tool source '{name}' has {property} {value}; it must be positive and at most {MaxTimeout}.", paramName);
            }
        }
    }

    /// <summary>What <see cref="ClientForAsync"/> found.</summary>
    private enum ClientState
    {
        /// <summary>A usable client.</summary>
        Connected,

        /// <summary>No connection could be made.</summary>
        Failed,

        /// <summary>The run was removed.</summary>
        Removed,

        /// <summary>The source was disposed.</summary>
        Disposed,
    }

    /// <summary>A client, and the cache entry holding it, when <paramref name="State"/> is <see cref="ClientState.Connected"/>.</summary>
    private readonly record struct Connection(ClientState State, McpClient? Client, CachedClient? Entry);

    /// <summary>A run's client, and the endpoint it was made for.</summary>
    private sealed record CachedClient(RunToolEndpoint Endpoint, Lazy<Task<McpClient>> Client);

    /// <summary>The wrapped tools and the schema list they were built from.</summary>
    private sealed record Routed(object Schemas, AITool[] Tools);

    /// <summary>A schema tool whose calls go to the calling run's endpoint, chosen by the turn's caller alone.</summary>
    private sealed class RoutedRemoteTool(AIFunction schema, RemoteRunToolSource source) : DelegatingAIFunction(schema)
    {
        /// <summary>The host server's tool, for an MCP-host source; null for local schemas.</summary>
        public McpClientTool? HostTool { get; } = schema as McpClientTool;

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            if (TurnScope.Current?.Caller is not { } caller || !caller.Claims.ContainsKey(RunWorkspaceClaims.RunId))
            {
                return source._host is not null
                    ? await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false) // no run: the host server
                    : source.RefuseNoRun();
            }

            return RunWorkspaceClaims.RunIdOf(caller) is { } runId
                ? await source.InvokeForRunAsync(this, runId, caller, arguments, cancellationToken).ConfigureAwait(false)
                : source.Refuse(runId: null, "the caller's run claim is not a valid run id.");
        }
    }

    [LoggerMessage(EventId = 340, Level = LogLevel.Warning, Message = "Refused a call to remote run tool source '{Source}' for run {RunId}: {Reason}")]
    private static partial void LogRefused(ILogger logger, string source, Guid? runId, string reason);

    [LoggerMessage(EventId = 341, Level = LogLevel.Information, Message = "Connecting remote run tool source '{Source}' to run {RunId} at {Endpoint}")]
    private static partial void LogConnecting(ILogger logger, string source, Guid runId, Uri endpoint);

    [LoggerMessage(EventId = 342, Level = LogLevel.Warning, Message = "Remote run tool source '{Source}' could not connect to run {RunId}: {ErrorType}")]
    private static partial void LogConnectFailed(ILogger logger, Exception exception, string source, Guid runId, string errorType);

    [LoggerMessage(EventId = 343, Level = LogLevel.Warning, Message = "Call to '{Tool}' on remote run tool source '{Source}' for run {RunId} did not finish within {Timeout} and was cancelled")]
    private static partial void LogCallTimedOut(ILogger logger, string source, string tool, Guid runId, TimeSpan timeout);

    [LoggerMessage(EventId = 344, Level = LogLevel.Warning, Message = "Call to '{Tool}' on remote run tool source '{Source}' for run {RunId} was cut off: the connection closed")]
    private static partial void LogCallCutOff(ILogger logger, Exception exception, string source, string tool, Guid runId);

    [LoggerMessage(EventId = 345, Level = LogLevel.Warning, Message = "Remote run tool source '{Source}' refused '{Tool}' for run {RunId}: {Reason}")]
    private static partial void LogCallRefused(ILogger logger, string source, string tool, Guid runId, string reason);

    [LoggerMessage(EventId = 346, Level = LogLevel.Error, Message = "A run tool call observer failed after '{Tool}' on remote run tool source '{Source}' for run {RunId}")]
    private static partial void LogObserverFailed(ILogger logger, Exception exception, string source, string tool, Guid runId);

    [LoggerMessage(EventId = 348, Level = LogLevel.Error, Message = "Remote run tool source '{Source}' could not resolve the endpoint of run {RunId}: {ErrorType}")]
    private static partial void LogResolveFailed(ILogger logger, Exception exception, string source, Guid runId, string errorType);

    [LoggerMessage(EventId = 349, Level = LogLevel.Warning, Message = "The sandbox of run {RunId} gave no usable answer to '{Tool}' on remote run tool source '{Source}': {ErrorType}: {Detail}")]
    private static partial void LogUnusableAnswer(ILogger logger, string source, string tool, Guid runId, string errorType, string detail);

    [LoggerMessage(EventId = 350, Level = LogLevel.Error, Message = "A run tool call observer did not finish within {Timeout} after '{Tool}' on remote run tool source '{Source}' for run {RunId}; the call went on")]
    private static partial void LogObserverTimedOut(ILogger logger, string source, string tool, Guid runId, TimeSpan timeout);

    [LoggerMessage(EventId = 351, Level = LogLevel.Debug, Message = "Remote run tool source '{Source}' disposed a client of run {RunId}")]
    private static partial void LogClientDisposed(ILogger logger, string source, Guid runId);

    [LoggerMessage(EventId = 347, Level = LogLevel.Warning, Message = "Disposing a client of remote run tool source '{Source}' failed: {ErrorType}")]
    private static partial void LogDisposeFailed(ILogger logger, Exception exception, string source, string errorType);
}
