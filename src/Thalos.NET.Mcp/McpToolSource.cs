using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ZeroAlloc.Results;

namespace Thalos.Mcp;

/// <summary>
/// One MCP server as a tool source. Connects lazily on the first <see cref="GetToolsAsync"/>, caches the client + tool list,
/// owns the stdio process, and retries the connection on the next call after a failure. Register as a singleton; disposed with the host
/// (<see cref="IAsyncDisposable"/> preferred; <see cref="IDisposable"/> blocks on the same path for synchronous container disposal).
/// </summary>
/// <remarks>
/// The tool list is cached for the lifetime of the source (no <c>tools/list_changed</c> handling). A server that dies mid-life is
/// <em>not</em> auto-reconnected in 0.1: cached <see cref="AIFunction"/>s keep pointing at the dead session and their invocations
/// fail; restart the host (or replace the source) to reconnect.
/// </remarks>
public sealed partial class McpToolSource : IToolSource, IAsyncDisposable, IDisposable
{
    private readonly McpServerDefinition _definition;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpToolSource> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly string _type;
    private McpClient? _client;
    private AITool[]? _tools;
    private bool _disposed;

    /// <summary>Creates a source named <paramref name="name"/> for <paramref name="definition"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> violates <see cref="ToolSourceName"/> or <paramref name="definition"/> is incomplete/unsupported.</exception>
    public McpToolSource(string name, McpServerDefinition definition, ILoggerFactory loggerFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ToolSourceName.ThrowIfInvalid(name, nameof(name));
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        Name = name;
        _definition = definition;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<McpToolSource>();
        _type = Validate(definition);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    /// <remarks>Connection/transport failures are returned as <see cref="AgentErrorCode.ProviderError"/>; the next call retries.</remarks>
    /// <exception cref="ObjectDisposedException">The source has been disposed.</exception>
    public async ValueTask<Result<IReadOnlyList<AITool>, AgentError>> GetToolsAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _tools) is { } cached)
        {
            return Result<IReadOnlyList<AITool>, AgentError>.Success(cached);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_tools is not null)
            {
                return Result<IReadOnlyList<AITool>, AgentError>.Success(_tools);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
            timeout.CancelAfter(_definition.Timeout);

            LogConnecting(_logger, Name, _type);
            var client = await McpClient.CreateAsync(CreateTransport(), clientOptions: null, _loggerFactory, timeout.Token).ConfigureAwait(false);
            IList<McpClientTool> tools;
            try
            {
                tools = await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _client = client;
            var snapshot = tools.Cast<AITool>().ToArray();
            Volatile.Write(ref _tools, snapshot);
            LogConnected(_logger, Name, snapshot.Length);
            return Result<IReadOnlyList<AITool>, AgentError>.Success(snapshot);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogConnectFailed(_logger, ex, Name, ex.Message);
            return Result<IReadOnlyList<AITool>, AgentError>.Failure(AgentError.ProviderError($"MCP server '{Name}' is unavailable.", ex.Message));
        }
        finally
        {
            _gate.Release();
        }
    }

    private IClientTransport CreateTransport() => _type switch
    {
        "stdio" => new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = Name,
            Command = _definition.Command!,
            Arguments = _definition.Args?.ToList(),
            EnvironmentVariables = _definition.Env?.ToDictionary(kv => kv.Key, kv => (string?)kv.Value, StringComparer.Ordinal),
            WorkingDirectory = _definition.Cwd,
            ShutdownTimeout = _definition.ShutdownTimeout,
        }, _loggerFactory),
        _ => new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = Name,
            Endpoint = new Uri(_definition.Url!),
            TransportMode = string.Equals(_type, "sse", StringComparison.Ordinal) ? HttpTransportMode.Sse : HttpTransportMode.StreamableHttp,
            AdditionalHeaders = _definition.Headers?.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            ConnectionTimeout = _definition.Timeout,
        }, _loggerFactory),
    };

    private static string Validate(McpServerDefinition definition)
    {
        var type = definition.EffectiveType;
        return type switch
        {
            "stdio" when !string.IsNullOrWhiteSpace(definition.Command) => type,
            "http" or "sse" when !string.IsNullOrWhiteSpace(definition.Url) => type,
            "stdio" => throw new ArgumentException("stdio MCP server requires Command", nameof(definition)),
            "http" or "sse" => throw new ArgumentException("http/sse MCP server requires Url", nameof(definition)),
            _ => throw new ArgumentException($"Unsupported MCP server type '{definition.Type}'. Use stdio, http or sse.", nameof(definition)),
        };
    }

    /// <summary>
    /// Disposes the MCP client (and, for stdio, shuts the server process down — waiting up to <see cref="McpServerDefinition.ShutdownTimeout"/>).
    /// Aborts an in-flight <see cref="GetToolsAsync"/> connection attempt (it returns <see cref="AgentErrorCode.ProviderError"/>) and waits
    /// for it to finish first; later calls throw <see cref="ObjectDisposedException"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _disposeCts.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_client is { } c)
            {
                _client = null;
                try
                {
                    await c.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogDisposeFailed(_logger, ex, Name);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
        _disposeCts.Dispose();
    }

    /// <summary>
    /// Synchronous <see cref="DisposeAsync"/> for hosts that tear the container down with a plain <c>ServiceProvider.Dispose()</c>
    /// (which does not await <see cref="IAsyncDisposable"/>s). Blocks for up to <see cref="McpServerDefinition.ShutdownTimeout"/> per stdio server.
    /// </summary>
    public void Dispose() =>
        // Blocking on the async path is deliberate here: the alternative (not implementing IDisposable) leaks the stdio process
        // when a synchronous container disposal is used. Prefer DisposeAsync / ServiceProvider.DisposeAsync() where possible.
        DisposeAsync().AsTask().GetAwaiter().GetResult();

    [LoggerMessage(EventId = 300, Level = LogLevel.Information, Message = "Connecting to MCP server '{Server}' ({Type})")]
    private static partial void LogConnecting(ILogger logger, string server, string type);

    [LoggerMessage(EventId = 301, Level = LogLevel.Information, Message = "MCP server '{Server}' connected: {ToolCount} tools")]
    private static partial void LogConnected(ILogger logger, string server, int toolCount);

    [LoggerMessage(EventId = 302, Level = LogLevel.Error, Message = "MCP server '{Server}' connection failed: {Error}")]
    private static partial void LogConnectFailed(ILogger logger, Exception exception, string server, string error);

    [LoggerMessage(EventId = 303, Level = LogLevel.Warning, Message = "Disposing MCP client '{Server}' failed")]
    private static partial void LogDisposeFailed(ILogger logger, Exception exception, string server);
}
