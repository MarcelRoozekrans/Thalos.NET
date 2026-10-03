using AwesomeAssertions.Execution;
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
    /// notifications/initialized. Red 2: in HttpMcpClientOptions.Create, pin ProtocolVersion to 2026-07-28 in place of
    /// the two infinite timeouts; the timed-out probe then fails the connection. Red 3: in GetClientToolsAsync, cache an
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
        await using var source = Source(_server);

        var tools = await source.GetToolsAsync(CancellationToken.None);

        tools.IsSuccess.Should().BeTrue(tools.IsFailure ? tools.Error.Message : "");
        tools.Value.OfType<AIFunction>().Select(f => f.Name).Should().Contain("list_files");
        _server.Requests.Should().Equal("/mcp/workspace server/discover", "/mcp/workspace tools/list");
    }

    /// <summary>
    /// With no handshake timeout of the SDK's own, the definition's Timeout alone bounds a connection whose handshake is
    /// never answered. This bound is wall-clock: McpToolSource takes no clock. A 2-second Timeout is checked against a
    /// 4-second ceiling, below the SDK's 5-second probe, so the probe path cannot satisfy it. Red: in
    /// GetClientToolsAsync, drop timeout.CancelAfter; the connection then never ends and the 20-second guard fails. Red 2:
    /// cancel after three times the Timeout; the failure then comes after 6 seconds and the ceiling fails.
    /// </summary>
    [Fact]
    public async Task An_http_handshake_that_is_never_answered_ends_at_the_definitions_timeout()
    {
        _server = await LoopbackSandbox.StartAsync(Path.Combine(_temp, "server"));
        _server.OnHandshake = ct => Task.Delay(Timeout.InfiniteTimeSpan, ct);
        await using var source = Source(_server, TimeSpan.FromSeconds(2));
        var timer = System.Diagnostics.Stopwatch.StartNew();

        var tools = await source.GetToolsAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(20));

        using var _ = new AssertionScope();
        tools.IsFailure.Should().BeTrue();
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4), "the 2-second Timeout ends the handshake");
    }

    /// <summary>
    /// An HTTP server that predates server/discover, answering it with a JSON-RPC error or a bare 400 or 404, is still
    /// connected through the initialize fallback. Red: in HttpMcpClientOptions.Create, pin ProtocolVersion to 2026-07-28;
    /// the fallback is then refused and the source is unavailable.
    /// </summary>
    [Theory]
    [InlineData("rpc-error")]
    [InlineData("400")]
    [InlineData("404")]
    public async Task An_http_server_that_predates_server_discover_is_connected_through_initialize(string answer)
    {
        _server = await LoopbackSandbox.StartAsync(Path.Combine(_temp, "server"));
        _server.AnswerDiscover = LoopbackSandbox.PredatingDiscover(answer);
        await using var source = Source(_server);

        var tools = await source.GetToolsAsync(CancellationToken.None);

        using var _ = new AssertionScope();
        tools.IsSuccess.Should().BeTrue(tools.IsFailure ? tools.Error.Message : "");
        _server.Requests.Take(2).Should().Equal("/mcp/workspace server/discover", "/mcp/workspace initialize");
    }

    /// <summary>
    /// The HTTP options leave the handshake to the caller's bound and keep the fallback for old servers. Red 1: drop
    /// InitializationTimeout from HttpMcpClientOptions.Create; it is then the SDK's 60 seconds, which would cut a longer
    /// ConnectTimeout. Red 2: drop DiscoverProbeTimeout; it is then 5 seconds. With InitializationTimeout infinite the SDK
    /// applies no probe timeout even then, so only this assertion pins the setting. Red 3: pin ProtocolVersion to
    /// 2026-07-28.
    /// </summary>
    [Fact]
    public void The_http_client_options_leave_the_handshake_to_the_callers_bound()
    {
        var options = HttpMcpClientOptions.Create();

        using var _ = new AssertionScope();
        options.InitializationTimeout.Should().Be(Timeout.InfiniteTimeSpan);
        options.DiscoverProbeTimeout.Should().Be(Timeout.InfiniteTimeSpan);
        options.ProtocolVersion.Should().BeNull("a server that predates server/discover is still fallen back to");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    private static McpToolSource Source(LoopbackSandbox server, TimeSpan? timeout = null) => new(
        "workspace",
        new McpServerDefinition
        {
            Type = "http",
            Url = server.Endpoint("workspace").Endpoint.ToString(),
            Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["Authorization"] = $"Bearer {server.Token}" },
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        },
        NullLoggerFactory.Instance);

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
