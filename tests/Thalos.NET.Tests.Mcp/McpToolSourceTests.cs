using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Mcp;

namespace Thalos.Tests.Mcp;

public sealed class McpToolSourceTests(McpServerFixture fixture) : IClassFixture<McpServerFixture>
{
    private McpToolSource Source => fixture.Source;

    [Fact]
    public async Task Lists_tools_from_stdio_server_and_caches()
    {
        var first = await Source.GetToolsAsync(default);
        first.IsSuccess.Should().BeTrue(first.IsFailure ? first.Error.ToString() : "");
        first.Value.Select(t => t.Name).Should().BeEquivalentTo(["echo", "add", "fail", "env"]);

        var second = await Source.GetToolsAsync(default);
        second.Value.Should().BeSameAs(first.Value, "tool list is cached per connection");
    }

    [Fact]
    public async Task Tools_are_invocable_AIFunctions()
    {
        var tools = (await Source.GetToolsAsync(default)).Value;
        var echo = (AIFunction)tools.Single(t => string.Equals(t.Name, "echo", StringComparison.Ordinal));
        var result = await echo.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { ["text"] = "hi" });
        result!.ToString().Should().Contain("echo:hi");
    }

    [Fact]
    public async Task Server_side_tool_error_surfaces_as_error_result_not_crash()
    {
        var tools = (await Source.GetToolsAsync(default)).Value;
        var fail = (AIFunction)tools.Single(t => string.Equals(t.Name, "fail", StringComparison.Ordinal));
        var result = await fail.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal));
        // The MCP C# SDK server sanitizes non-McpException messages ("boom" is not leaked); the observable contract is an isError result, not a thrown exception.
        var text = result!.ToString();
        text.Should().Contain("\"isError\":true");
        text.Should().Contain("An error occurred invoking");
    }

    [Fact]
    public async Task Env_is_passed_to_child_process()
    {
        var definition = McpServerFixture.Definition();
        definition.Env = new Dictionary<string, string>(StringComparer.Ordinal) { ["THALOS_MCP_TEST_VALUE"] = "from-thalos" };
        await using var source = new McpToolSource("envsrc", definition, NullLoggerFactory.Instance);

        var tools = (await source.GetToolsAsync(default)).Value;
        var env = (AIFunction)tools.Single(t => string.Equals(t.Name, "env", StringComparison.Ordinal));
        var result = await env.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { ["name"] = "THALOS_MCP_TEST_VALUE" });
        result!.ToString().Should().Contain("from-thalos");
    }

    [Fact]
    public async Task Synchronous_Dispose_shuts_the_source_down_and_further_calls_throw()
    {
        // plain ServiceProvider.Dispose() only sees IDisposable — the source must not leak the stdio process on that path
        var source = new McpToolSource("sync", McpServerFixture.Definition(), NullLoggerFactory.Instance);
        (await source.GetToolsAsync(default)).IsSuccess.Should().BeTrue();

        var sw = Stopwatch.StartNew();
        source.Dispose();
        source.Dispose(); // idempotent
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));

        var act = async () => await source.GetToolsAsync(default);
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Unreachable_server_returns_ProviderError_not_exception()
    {
        await using var bad = new McpToolSource("bad", new McpServerDefinition { Type = "stdio", Command = "definitely-not-a-command-xyz", Timeout = TimeSpan.FromSeconds(5) }, NullLoggerFactory.Instance);
        var r = await bad.GetToolsAsync(default);
        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be(AgentErrorCode.ProviderError);
    }

    [Fact]
    public async Task Dispose_during_connect_does_not_hang()
    {
        // --delay-ms keeps the server silent, so the connect is still in flight when we dispose.
        var source = new McpToolSource("slow", McpServerFixture.Definition("--delay-ms", "8000"), NullLoggerFactory.Instance);
        var sw = Stopwatch.StartNew();
        var connect = source.GetToolsAsync(default).AsTask();
        await Task.Delay(100);
        var dispose = source.DisposeAsync().AsTask();

        var both = Task.WhenAll(connect, dispose);
        var finished = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(10)));
        finished.Should().BeSameAs(both, "dispose must abort the in-flight connect and both must complete");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));

        var r = await connect;
        r.IsFailure.Should().BeTrue("the aborted connect is reported as ProviderError, not thrown");
        r.Error.Code.Should().Be(AgentErrorCode.ProviderError);
    }

    [Fact]
    public async Task Disposed_source_throws_ObjectDisposedException()
    {
        var source = new McpToolSource("gone", McpServerFixture.Definition(), NullLoggerFactory.Instance);
        await source.DisposeAsync();
        await source.DisposeAsync(); // idempotent

        var act = async () => await source.GetToolsAsync(default);
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public void Unsupported_type_is_rejected_at_construction()
    {
        var act = () => new McpToolSource("x", new McpServerDefinition { Type = "carrier-pigeon" }, NullLoggerFactory.Instance);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("bad__name")]
    [InlineData("bad.name")]
    [InlineData(" ")]
    public void Invalid_source_name_is_rejected_at_construction(string name)
    {
        var act = () => new McpToolSource(name, McpServerFixture.Definition(), NullLoggerFactory.Instance);
        act.Should().Throw<ArgumentException>().WithParameterName(nameof(name));
    }

    [Fact]
    public void Null_arguments_are_rejected_at_construction()
    {
        var noDefinition = () => new McpToolSource("x", null!, NullLoggerFactory.Instance);
        var noLoggerFactory = () => new McpToolSource("x", McpServerFixture.Definition(), null!);
        noDefinition.Should().Throw<ArgumentNullException>();
        noLoggerFactory.Should().Throw<ArgumentNullException>();
    }
}
