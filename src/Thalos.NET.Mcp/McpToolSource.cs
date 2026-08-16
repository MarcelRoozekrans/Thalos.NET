using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ZeroAlloc.Results;

namespace Thalos.Mcp;

/// <summary>
/// One MCP server as a tool source. Connects lazily, caches the client + tool list, owns the stdio process,
/// reconnects on the next call after a failure. Register as a singleton; disposed with the host.
/// </summary>
public sealed partial class McpToolSource(string name, McpServerDefinition definition, ILoggerFactory loggerFactory) : IToolSource, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<McpToolSource> _logger = loggerFactory.CreateLogger<McpToolSource>();
    private McpClient? _client;
    private AITool[]? _tools;
    private readonly string _type = Validate(definition);

    /// <inheritdoc />
    public string Name { get; } = name;

    /// <inheritdoc />
    /// <remarks>Connection/transport failures are returned as <see cref="AgentErrorCode.ProviderError"/>; the next call retries.</remarks>
    public async ValueTask<Result<IReadOnlyList<AITool>, AgentError>> GetToolsAsync(CancellationToken ct)
    {
        if (_tools is not null)
        {
            return Result<IReadOnlyList<AITool>, AgentError>.Success(_tools);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_tools is not null)
            {
                return Result<IReadOnlyList<AITool>, AgentError>.Success(_tools);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(definition.Timeout);

            LogConnecting(_logger, Name, _type);
            var client = await McpClient.CreateAsync(CreateTransport(), clientOptions: null, loggerFactory, timeout.Token).ConfigureAwait(false);
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);

            _client = client;
            _tools = tools.Cast<AITool>().ToArray();
            LogConnected(_logger, Name, _tools.Length);
            return Result<IReadOnlyList<AITool>, AgentError>.Success(_tools);
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
            Command = definition.Command!,
            Arguments = definition.Args?.ToList(),
            EnvironmentVariables = definition.Env?.ToDictionary(kv => kv.Key, kv => (string?)kv.Value, StringComparer.Ordinal),
            WorkingDirectory = definition.Cwd,
        }, loggerFactory),
        _ => new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = Name,
            Endpoint = new Uri(definition.Url!),
            AdditionalHeaders = definition.Headers?.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            ConnectionTimeout = definition.Timeout,
        }, loggerFactory),
    };

    private static string Validate(McpServerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
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

    /// <summary>Disposes the MCP client (and, for stdio, shuts the server process down).</summary>
    public async ValueTask DisposeAsync()
    {
        if (_client is { } c)
        {
            try
            {
                await c.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogDisposeFailed(_logger, ex, Name);
            }
        }

        _gate.Dispose();
    }

    [LoggerMessage(EventId = 300, Level = LogLevel.Information, Message = "Connecting to MCP server '{Server}' ({Type})")]
    private static partial void LogConnecting(ILogger logger, string server, string type);

    [LoggerMessage(EventId = 301, Level = LogLevel.Information, Message = "MCP server '{Server}' connected: {ToolCount} tools")]
    private static partial void LogConnected(ILogger logger, string server, int toolCount);

    [LoggerMessage(EventId = 302, Level = LogLevel.Error, Message = "MCP server '{Server}' connection failed: {Error}")]
    private static partial void LogConnectFailed(ILogger logger, Exception exception, string server, string error);

    [LoggerMessage(EventId = 303, Level = LogLevel.Warning, Message = "Disposing MCP client '{Server}' failed")]
    private static partial void LogDisposeFailed(ILogger logger, Exception exception, string server);
}
