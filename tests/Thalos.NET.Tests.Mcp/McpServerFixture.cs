using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Mcp;

namespace Thalos.Tests.Mcp;

/// <summary>One connected <see cref="McpToolSource"/> over the stdio test server, shared by the read-only tests of a class.</summary>
public sealed class McpServerFixture : IAsyncLifetime, IAsyncDisposable
{
    // The server exe is built next to this test project (same configuration/TFM); ReferenceOutputAssembly=false in the csproj ensures it builds first.
    public static string ServerDll => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory.Replace("Thalos.NET.Tests.Mcp", "Thalos.NET.Tests.McpServer", StringComparison.Ordinal),
        "Thalos.NET.Tests.McpServer.dll"));

    public static McpServerDefinition Definition(params string[] extraArgs) => new()
    {
        Type = "stdio",
        Command = "dotnet",
        Args = [ServerDll, .. extraArgs],
        Timeout = TimeSpan.FromSeconds(30),
        ShutdownTimeout = TimeSpan.FromSeconds(1),
    };

    public McpToolSource Source { get; private set; } = null!;

    public Task InitializeAsync()
    {
        File.Exists(ServerDll).Should().BeTrue($"build tests/Thalos.NET.Tests.McpServer first ({ServerDll})");
        Source = new McpToolSource("echo", Definition(), NullLoggerFactory.Instance);
        return Task.CompletedTask;
    }

    // xUnit 2.x IAsyncLifetime is Task-based; IAsyncDisposable is implemented too so CA1001 sees the field owner as disposable.
    public async Task DisposeAsync() => await Source.DisposeAsync();

    ValueTask IAsyncDisposable.DisposeAsync() => new(DisposeAsync());
}
