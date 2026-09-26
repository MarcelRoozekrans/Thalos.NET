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
            "legacy":   { "command": "npx", "args": ["-y", "memorylens-mcp"], "timeout": "00:01:00", "shutdownTimeout": "00:00:01" }
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
        servers["legacy"].Timeout.Should().Be(TimeSpan.FromMinutes(1));
        servers["legacy"].ShutdownTimeout.Should().Be(TimeSpan.FromSeconds(1));
        servers["roslyn"].ShutdownTimeout.Should().Be(TimeSpan.FromSeconds(2), "Thalos default when not specified (the SDK waits the full timeout on dispose)");
    }
    [Fact]
    public void Binds_a_runScoped_section()
    {
        const string json = """
        {
          "mcpServers": {
            "roslyn": {
              "command": "dnx",
              "args": ["RoslynCodeLens.Mcp", "--", "C:/host/App.sln"],
              "runScoped": {
                "args": ["RoslynCodeLens.Mcp", "--", "${run.workspace.solution}"],
                "env": { "RUN": "${run.id}" },
                "cwd": "${run.workspace.root}/src",
                "readyTool": "list_solutions",
                "reload": "tool:rebuild_solution"
              }
            },
            "plain": { "command": "npx" }
          }
        }
        """;
        var servers = McpConfigFile.Parse(json);

        servers["roslyn"].RunScoped.Should().NotBeNull();
        var runScoped = servers["roslyn"].RunScoped!;
        runScoped.Args.Should().Equal("RoslynCodeLens.Mcp", "--", "${run.workspace.solution}");
        runScoped.Env.Should().BeEquivalentTo(new Dictionary<string, string>(StringComparer.Ordinal) { ["RUN"] = "${run.id}" });
        runScoped.Cwd.Should().Be("${run.workspace.root}/src");
        runScoped.ReadyTool.Should().Be("list_solutions");
        runScoped.Reload.Should().Be("tool:rebuild_solution");
        servers["plain"].RunScoped.Should().BeNull("an entry without runScoped is host-wide only");
    }
}
