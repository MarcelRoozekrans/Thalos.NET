using Thalos.Mcp;

namespace Thalos.Tests.Mcp;

public sealed class McpConfigFileTests
{
    [Fact]
    public void Parses_claude_code_style_mcp_json()
    {
        const string json = """
        {
          "mcpServers": {
            "roslyn": { "type": "stdio", "command": "dnx", "args": ["RoslynCodeLens.Mcp", "--", "C:/x/x.sln"], "env": { "ROSLYN_CODELENS_OPEN_PROJECT_TIMEOUT_SECONDS": "600" } },
            "context7": { "type": "http", "url": "https://context7.com/api", "headers": { "Authorization": "Bearer t" } },
            "legacy":   { "command": "npx", "args": ["-y", "memorylens-mcp"] }
          }
        }
        """;
        var servers = McpConfigFile.Parse(json);

        servers.Should().HaveCount(3);
        servers["roslyn"].Type.Should().Be("stdio");
        servers["roslyn"].Args.Should().Equal("RoslynCodeLens.Mcp", "--", "C:/x/x.sln");
        servers["roslyn"].Env!["ROSLYN_CODELENS_OPEN_PROJECT_TIMEOUT_SECONDS"].Should().Be("600");
        servers["context7"].Url.Should().Be("https://context7.com/api");
        servers["context7"].Headers!["Authorization"].Should().Be("Bearer t");
        servers["legacy"].EffectiveType.Should().Be("stdio", "type defaults to stdio when a command is present");
    }
}
