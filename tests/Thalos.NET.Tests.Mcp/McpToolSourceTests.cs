using System.Diagnostics;
using System.Globalization;
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
        first.Value.Select(t => t.Name).Should().BeEquivalentTo(["echo", "add", "fail", "env", "args", "cwd", "ready_after", "reload_count", "slow", "overlaps", "pid"]);

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

    /// <summary>
    /// A plain <c>ServiceProvider.Dispose()</c> only sees <see cref="IDisposable"/>, so the synchronous Dispose must shut
    /// the stdio server down, not leak its process. Asserted by the server's process ending, not by how long the dispose
    /// took: that is the SDK's teardown, which spends the shutdown timeout as a grace period and, on a loaded Windows
    /// runner where other tests' stdio sessions pin thread-pool threads in blocking pipe reads, has taken well over 10 s.
    /// Red, process ended: in McpToolSource.DisposeAsync, skip disposing the client.
    /// Red, disposed: drop the first <c>ObjectDisposedException.ThrowIf</c> from GetClientToolsAsync.
    /// </summary>
    [Fact]
    public async Task Synchronous_Dispose_shuts_the_source_down_and_further_calls_throw()
    {
        var source = new McpToolSource("sync", McpServerFixture.Definition(), NullLoggerFactory.Instance);
        var tools = await source.GetToolsAsync(default);
        tools.IsSuccess.Should().BeTrue(tools.IsFailure ? tools.Error.ToString() : "");
        var pid = await PidAsync(tools.Value);

        source.Dispose();
        source.Dispose(); // idempotent

        await UntilAsync(() => !IsRunning(pid), "the disposed source's server process to end");
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

    /// <summary>
    /// Disposing a source aborts its in-flight connect instead of waiting for the server's first answer. The server stays
    /// silent for ten minutes and the definition's connect timeout is ten minutes too, so only the dispose, or the SDK's
    /// own 60 s initialization timeout, can end the connect, and the failure's detail names which: a cancellation, or a
    /// <see cref="TimeoutException"/>. How long the abort's teardown takes is not asserted; on a loaded Windows runner,
    /// where other tests' stdio sessions pin thread-pool threads in blocking pipe reads, it has taken over 10 s. The
    /// two-minute bound is a hang guard only.
    /// Red, both complete: in McpToolSource.GetClientToolsAsync, drop <c>_gate.Release()</c> from the finally.
    /// Red, a result, not thrown: rethrow from GetClientToolsAsync's last catch.
    /// Red, aborted by the dispose: drop <c>_disposeCts.CancelAsync()</c> from DisposeAsync; the connect then ends at the
    /// SDK's initialization timeout.
    /// </summary>
    [Fact]
    public async Task Dispose_during_connect_does_not_hang()
    {
        var definition = McpServerFixture.Definition("--delay-ms", "600000"); // silent: the connect is in flight when we dispose
        definition.Timeout = TimeSpan.FromMinutes(10);
        var source = new McpToolSource("slow", definition, NullLoggerFactory.Instance);
        var connect = source.GetToolsAsync(default).AsTask();
        await Task.Delay(100);
        var dispose = source.DisposeAsync().AsTask();

        var both = Task.WhenAll(connect, dispose);
        var finished = await Task.WhenAny(both, Task.Delay(TimeSpan.FromMinutes(2)));
        finished.Should().BeSameAs(both, "dispose must abort the in-flight connect and both must complete");

        var r = await connect;
        r.IsFailure.Should().BeTrue("the aborted connect is reported as ProviderError, not thrown");
        r.Error.Code.Should().Be(AgentErrorCode.ProviderError);
        r.Error.Detail.Should().BeOneOf([nameof(TaskCanceledException), nameof(OperationCanceledException)], "the dispose cancelled the connect; no timeout ended it");
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

    /// <summary>The process id of the server behind <paramref name="tools"/>, from its <c>pid</c> tool.</summary>
    private static async Task<int> PidAsync(IReadOnlyList<AITool> tools)
    {
        var pid = (AIFunction)tools.Single(t => string.Equals(t.Name, "pid", StringComparison.Ordinal));
        var result = (TextContent)(await pid.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal)))!;
        return int.Parse(result.Text, CultureInfo.InvariantCulture);
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no process has that id any more
        }
    }

    /// <summary>Waits until <paramref name="condition"/> holds; the bound is a hang guard for a process the kernel is still tearing down.</summary>
    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            sw.Elapsed.Should().BeLessThan(McpServerFixture.StartupBudget, $"waiting for {what}");
            await Task.Delay(50);
        }
    }
}
