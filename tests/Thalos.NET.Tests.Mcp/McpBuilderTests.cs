using AwesomeAssertions.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Thalos.Mcp;
using Thalos.Tools;
using Thalos.Workspaces;

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
            tools.Value.Select(t => t.Name).Should().BeEquivalentTo(["echo__echo", "echo__add", "echo__fail", "echo__env", "echo__args", "echo__cwd", "echo__ready_after", "echo__reload_count", "echo__slow", "echo__overlaps", "echo__pid"]);
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
    public void AddMcpServer_resolves_without_logging_registered()
    {
        var services = new ServiceCollection(); // no AddLogging()
        services.AddThalos(t => t.AddMcpServer("echo", McpServerFixture.Definition()));
        using var sp = services.BuildServiceProvider();

        sp.GetServices<IToolSource>().Should().ContainSingle().Which.Should().BeOfType<McpToolSource>();
    }

    [Fact]
    public void AddMcpServer_rejects_invalid_source_name_at_composition()
    {
        var services = new ServiceCollection();
        var act = () => services.AddThalos(t => t.AddMcpServer("bad__name", McpServerFixture.Definition()));
        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("name");
    }

    // ---------- run-scoped entries ----------

    [Fact]
    public async Task An_entry_with_runScoped_registers_the_routed_source_and_the_registry()
    {
        var services = new ServiceCollection();
        services.AddThalos(t => t.AddMcpServer("roslyn", Definition(runScoped: true)));
        await using var sp = services.BuildServiceProvider();

        using var _scope = new AssertionScope();
        sp.GetServices<IToolSource>().Should().ContainSingle(s => string.Equals(s.Name, "roslyn", StringComparison.Ordinal)).Which.Should().BeOfType<RunScopedMcpToolSource>();
        sp.GetServices<IRunWorkspaceObserver>().Should().ContainSingle().Which.Should().BeSameAs(sp.GetRequiredService<IRunToolServerReadiness>());
    }

    [Fact]
    public async Task With_no_workspace_provider_the_source_still_builds_and_a_run_caller_is_refused()
    {
        var services = new ServiceCollection();
        services.AddThalos(t => t.AddMcpServer("roslyn", Definition(runScoped: true))); // no IRunWorkspaceProvider registered
        await using var sp = services.BuildServiceProvider();

        var source = sp.GetServices<IToolSource>().Single(s => string.Equals(s.Name, "roslyn", StringComparison.Ordinal));
        var tool = (await source.GetToolsAsync(CancellationToken.None)).Value.OfType<AIFunction>().Single(t => string.Equals(t.Name, "args", StringComparison.Ordinal));
        string result;
        using (TestCallers.BeginTurn(TestCallers.RunCaller(Guid.NewGuid())))
        {
            result = (await tool.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal), CancellationToken.None))!.ToString()!;
        }

        string hostResult;
        using (TestCallers.BeginTurn(new TestCaller("chat-user")))
        {
            hostResult = (await tool.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal), CancellationToken.None))!.ToString()!;
        }

        using var _scope = new AssertionScope();
        result.Should().StartWith("error: run tool server 'roslyn' is not available for this run: ");
        result.Should().Contain("no run workspace provider");
        result.Should().NotContain("--host");
        hostResult.Should().Contain("--host", "with no workspace provider, host callers are still served by the host server");
    }

    [Fact]
    public async Task Every_run_scoped_entry_shares_one_registry_under_every_interface()
    {
        var services = new ServiceCollection();
        services.AddThalos(t => t.AddMcpServer("roslyn", Definition(runScoped: true)).AddMcpServer("plain", McpServerFixture.Definition()).AddMcpServer("second", Definition(runScoped: true)));
        await using var sp = services.BuildServiceProvider();

        var registry = sp.GetRequiredService<RunMcpServerRegistry>();
        using var _scope = new AssertionScope();
        sp.GetServices<IRunWorkspaceObserver>().Should().ContainSingle().Which.Should().BeSameAs(registry);
        sp.GetServices<IRunWorkspaceChangeListener>().Should().ContainSingle().Which.Should().BeSameAs(registry);
        sp.GetRequiredService<IRunToolServerReadiness>().Should().BeSameAs(registry);
        sp.GetServices<IToolSource>().Select(s => (s.Name, s.GetType())).Should().BeEquivalentTo(
            [("roslyn", typeof(RunScopedMcpToolSource)), ("plain", typeof(McpToolSource)), ("second", typeof(RunScopedMcpToolSource))]);
    }

    [Fact]
    public async Task An_entry_without_runScoped_registers_no_registry()
    {
        var services = new ServiceCollection();
        services.AddThalos(t => t.AddMcpServer("plain", McpServerFixture.Definition()));
        await using var sp = services.BuildServiceProvider();

        using var _scope = new AssertionScope();
        sp.GetService<RunMcpServerRegistry>().Should().BeNull();
        sp.GetServices<IRunWorkspaceObserver>().Should().BeEmpty();
        sp.GetService<IRunToolServerReadiness>().Should().BeNull();
    }

    [Fact]
    public void A_second_run_scoped_entry_with_the_same_name_is_rejected_at_composition()
    {
        var services = new ServiceCollection();
        var act = () => services.AddThalos(t => t.AddMcpServer("roslyn", Definition(runScoped: true)).AddMcpServer("roslyn", Definition(runScoped: true)));
        act.Should().Throw<ArgumentException>().WithParameterName("name").WithMessage("*'roslyn' was already added*");
    }

    [Fact]
    public void A_run_scoped_entry_named_like_an_earlier_plain_entry_is_rejected_at_composition()
    {
        var services = new ServiceCollection();
        var act = () => services.AddThalos(t => t.AddMcpServer("roslyn", Definition(runScoped: false)).AddMcpServer("roslyn", Definition(runScoped: true)));
        act.Should().Throw<ArgumentException>().WithParameterName("name").WithMessage("*'roslyn' was already added*");
    }

    [Fact]
    public void A_plain_entry_named_like_an_earlier_run_scoped_entry_is_rejected_at_composition()
    {
        var services = new ServiceCollection();
        var act = () => services.AddThalos(t => t.AddMcpServer("roslyn", Definition(runScoped: true)).AddMcpServer("roslyn", Definition(runScoped: false)));
        act.Should().Throw<ArgumentException>().WithParameterName("name").WithMessage("*'roslyn' was already added*");
    }

    [Fact]
    public void Two_plain_entries_with_the_same_name_are_still_accepted_first_wins_as_before()
    {
        var services = new ServiceCollection();
        var act = () => services.AddThalos(t => t.AddMcpServer("echo", McpServerFixture.Definition()).AddMcpServer("echo", McpServerFixture.Definition()));
        act.Should().NotThrow();
        services.Count(d => d.ServiceType == typeof(IToolSource)).Should().Be(2, "both are registered; the catalog keeps the first");
    }

    private static McpServerDefinition Definition(bool runScoped)
    {
        var definition = McpServerFixture.Definition("--host");
        definition.RunScoped = runScoped ? new RunScopedMcpDefinition { Args = [McpServerFixture.ServerDll, "--run"] } : null;
        return definition;
    }
}
