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

    /// <summary>
    /// How long a test gives the stdio test server it starts to come up: the connect timeout of <see cref="Definition"/>
    /// and the readiness waits of the tests that start their own server. Generous on purpose: each start is a cold
    /// <c>dotnet</c> process, which on a loaded CI runner, or under the synthetic load used to reproduce CI failures, has
    /// taken over 30 s, and how fast a server starts is not what those tests are about. A test whose subject is a
    /// timeout sets its own.
    /// </summary>
    public static readonly TimeSpan StartupBudget = TimeSpan.FromMinutes(2);

    public static McpServerDefinition Definition(params string[] extraArgs) => new()
    {
        Type = "stdio",
        Command = "dotnet",
        Args = [ServerDll, .. extraArgs],
        Timeout = StartupBudget,
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
