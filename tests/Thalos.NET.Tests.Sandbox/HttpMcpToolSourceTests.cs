using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Mcp;

namespace Thalos.Tests.Sandbox;

/// <summary>
/// <see cref="McpToolSource"/> over Streamable HTTP, against a real sandbox host on loopback Kestrel standing in for a
/// host-wide HTTP MCP server.
/// </summary>
public sealed class HttpMcpToolSourceTests : IAsyncLifetime
{
    private readonly string _temp = Directory.CreateTempSubdirectory("thalos-http-mcp-").FullName;
    private LoopbackSandbox? _server;

    /// <summary>
    /// An HTTP server that takes longer than the MCP SDK's 5-second <c>server/discover</c> probe to answer its first
    /// handshake is waited for within the definition's Timeout and connected with that handshake, not taken for a server
    /// that predates <c>server/discover</c> and sent <c>initialize</c>. Red 1: in McpToolSource.GetClientToolsAsync, pass
    /// null options for an http server too; the probe then times out, and the server also receives initialize and
    /// notifications/initialized. Red 2: in HttpMcpClientOptions.Create, pin ProtocolVersion to 2026-07-28 instead of
    /// lifting the probe timeout; the timed-out probe then fails the connection. Red 3: in GetClientToolsAsync, cache an
    /// empty snapshot; list_files is then missing.
    /// </summary>
    [Fact]
    public async Task An_http_server_slow_to_answer_its_first_handshake_is_waited_for_not_taken_for_an_old_server()
    {
        _server = await LoopbackSandbox.StartAsync(Path.Combine(_temp, "server"));
        var slow = 1;
        _server.OnHandshake = async ct =>
        {
            if (Interlocked.Exchange(ref slow, 0) == 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(6), ct);
            }
        };
        var definition = new McpServerDefinition
        {
            Type = "http",
            Url = _server.Endpoint("workspace").Endpoint.ToString(),
            Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["Authorization"] = $"Bearer {_server.Token}" },
        };
        await using var source = new McpToolSource("workspace", definition, NullLoggerFactory.Instance);

        var tools = await source.GetToolsAsync(CancellationToken.None);

        tools.IsSuccess.Should().BeTrue(tools.IsFailure ? tools.Error.Message : "");
        tools.Value.OfType<AIFunction>().Select(f => f.Name).Should().Contain("list_files");
        _server.Requests.Should().Equal("/mcp/workspace server/discover", "/mcp/workspace tools/list");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // a file still held by the host; the temp directory is cleaned up by the OS
        }
    }
}
