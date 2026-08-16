using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Mcp;

namespace Thalos.Tests.Mcp;

public sealed class McpToolSourceTests : IAsyncLifetime, IAsyncDisposable
{
    // The server exe is built next to this test project (same configuration/TFM); ReferenceOutputAssembly=false in the csproj ensures it builds first.
    private static string ServerDll => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory.Replace("Thalos.NET.Tests.Mcp", "Thalos.NET.Tests.McpServer", StringComparison.Ordinal),
        "Thalos.NET.Tests.McpServer.dll"));

    private McpToolSource _source = null!;

    public Task InitializeAsync()
    {
        File.Exists(ServerDll).Should().BeTrue($"build tests/Thalos.NET.Tests.McpServer first ({ServerDll})");
        _source = new McpToolSource("echo", new McpServerDefinition { Type = "stdio", Command = "dotnet", Args = [ServerDll], Timeout = TimeSpan.FromSeconds(30) }, NullLoggerFactory.Instance);
        return Task.CompletedTask;
    }

    // xUnit 2.x IAsyncLifetime is Task-based; IAsyncDisposable is implemented too so CA1001 sees the field owner as disposable.
    public async Task DisposeAsync() => await _source.DisposeAsync();

    ValueTask IAsyncDisposable.DisposeAsync() => new(DisposeAsync());

    [Fact]
    public async Task Lists_tools_from_stdio_server_and_caches()
    {
        var first = await _source.GetToolsAsync(default);
        first.IsSuccess.Should().BeTrue(first.IsFailure ? first.Error.ToString() : "");
        first.Value.Select(t => t.Name).Should().BeEquivalentTo(["echo", "add", "fail"]);

        var second = await _source.GetToolsAsync(default);
        second.Value.Should().BeSameAs(first.Value, "tool list is cached per connection");
    }

    [Fact]
    public async Task Tools_are_invocable_AIFunctions()
    {
        var tools = (await _source.GetToolsAsync(default)).Value;
        var echo = (AIFunction)tools.Single(t => string.Equals(t.Name, "echo", StringComparison.Ordinal));
        var result = await echo.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { ["text"] = "hi" });
        result!.ToString().Should().Contain("echo:hi");
    }

    [Fact]
    public async Task Server_side_tool_error_surfaces_as_error_result_not_crash()
    {
        var tools = (await _source.GetToolsAsync(default)).Value;
        var fail = (AIFunction)tools.Single(t => string.Equals(t.Name, "fail", StringComparison.Ordinal));
        var result = await fail.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal));
        // The MCP C# SDK server sanitizes non-McpException messages ("boom" is not leaked); the observable contract is an isError result, not a thrown exception.
        var text = result!.ToString();
        text.Should().Contain("\"isError\":true");
        text.Should().Contain("An error occurred invoking");
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
    public void Unsupported_type_is_rejected_at_construction()
    {
        var act = () => new McpToolSource("x", new McpServerDefinition { Type = "carrier-pigeon" }, NullLoggerFactory.Instance);
        act.Should().Throw<ArgumentException>();
    }
}
