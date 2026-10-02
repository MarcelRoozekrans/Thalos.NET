using Thalos.Mcp;

namespace Thalos.Tests.Mcp;

public sealed class StdioEnvironmentTests : IAsyncLifetime
{
    private const string Secret = "THALOS_TEST_SECRET_SHOULD_NOT_LEAK";
    private readonly List<IAsyncDisposable> _sources = [];

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable(Secret, "leaked");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable(Secret, null);
        foreach (var s in _sources)
        {
            await s.DisposeAsync();
        }
    }

    /// <summary>Red: drop <c>InheritEnvironmentVariables = false</c> in McpToolSource.CreateTransport.</summary>
    [Fact]
    public async Task A_host_variable_does_not_reach_a_stdio_server()
    {
        var value = await EnvOf(McpServerFixture.Definition(), Secret);
        value.Should().Be("<unset>");
    }

    /// <summary>Red: skip the PassEnvironment loop in StdioEnvironment.Build.</summary>
    [Fact]
    public async Task A_passed_variable_reaches_the_server()
    {
        var definition = McpServerFixture.Definition();
        definition.PassEnvironment = [Secret];
        (await EnvOf(definition, Secret)).Should().Be("leaked");
    }

    /// <summary>Red: seed from an empty dictionary instead of GetDefaultEnvironmentVariables.</summary>
    [Fact]
    public async Task PATH_still_reaches_the_server_so_it_can_start()
    {
        (await EnvOf(McpServerFixture.Definition(), "PATH")).Should().NotBe("<unset>");
    }

    /// <summary>Red: apply Env before the defaults instead of after.</summary>
    [Fact]
    public async Task The_definitions_env_wins_over_a_default()
    {
        var definition = McpServerFixture.Definition();
        definition.Env = new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = Environment.GetEnvironmentVariable("PATH") + Path.PathSeparator + "marker" };
        (await EnvOf(definition, "PATH")).Should().EndWith("marker");
    }

    /// <summary>Red: apply Env before the PassEnvironment loop instead of after.</summary>
    [Fact]
    public async Task The_definitions_env_wins_over_a_passed_variable()
    {
        var definition = McpServerFixture.Definition();
        definition.PassEnvironment = [Secret];
        definition.Env = new Dictionary<string, string>(StringComparer.Ordinal) { [Secret] = "from-env" };
        (await EnvOf(definition, Secret)).Should().Be("from-env");
    }

    private async Task<string> EnvOf(McpServerDefinition definition, string name)
    {
        var source = new McpToolSource("env", definition, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        _sources.Add(source);
        var tools = await source.GetClientToolsAsync(CancellationToken.None);
        var env = tools.Value.Single(t => string.Equals(t.Name, "env", StringComparison.Ordinal));
        var result = await env.CallAsync(new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = name });
        return result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text;
    }
}
