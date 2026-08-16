using Microsoft.Extensions.DependencyInjection;
using Thalos.Mcp;
using Thalos.Tools;

namespace Thalos.Tests.Mcp;

public sealed class McpBuilderTests
{
    [Fact]
    public async Task AddMcpServersFromFile_end_to_end_yields_qualified_tools_through_the_catalog()
    {
        var dir = Directory.CreateTempSubdirectory("thalos-mcp-");
        try
        {
            var dll = McpServerFixture.ServerDll.Replace('\\', '/');
            var path = Path.Combine(dir.FullName, ".mcp.json");
            await File.WriteAllTextAsync(path, $$"""
                {
                  "mcpServers": {
                    "echo": { "type": "stdio", "command": "dotnet", "args": ["{{dll}}"], "shutdownTimeout": "00:00:01" }
                  }
                }
                """);

            var services = new ServiceCollection().AddLogging();
            services.AddThalos(t => t.AddMcpServersFromFile(path));
            await using var sp = services.BuildServiceProvider();

            var catalog = sp.GetRequiredService<IToolCatalog>();
            var tools = await catalog.ResolveAsync(new AgentDefinition { Id = AgentId.New(), Name = "a", Instructions = "i" }, default);

            tools.IsSuccess.Should().BeTrue();
            tools.Value.Select(t => t.Name).Should().BeEquivalentTo(["echo__echo", "echo__add", "echo__fail", "echo__env"]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void AddMcpServersFromFile_with_missing_file_is_a_no_op()
    {
        var services = new ServiceCollection();
        var act = () => services.AddThalos(t => t.AddMcpServersFromFile(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".json")));
        act.Should().NotThrow();
        services.Should().NotContain(d => d.ServiceType == typeof(IToolSource));
    }

    [Fact]
    public void AddMcpServer_rejects_invalid_source_name_at_composition()
    {
        var services = new ServiceCollection();
        var act = () => services.AddThalos(t => t.AddMcpServer("bad__name", McpServerFixture.Definition()));
        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("name");
    }
}
