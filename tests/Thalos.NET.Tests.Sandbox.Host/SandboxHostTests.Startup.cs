using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Thalos.Sandbox;
using Thalos.Tools;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Tests.Sandbox.Host;

public sealed partial class SandboxHostTests
{
    /// <summary>A tool class whose only binder in the test below names a method that is not a tool.</summary>
    [ThalosToolType]
    public sealed class BadBinderTools
    {
        [ThalosTool("ok")]
        public static string Ok() => "ok";

        public static string NotATool() => "no";
    }

    private static string Shape(AIFunction tool) =>
        $"{tool.Name}\n{tool.Description}\n{tool.JsonSchema.GetRawText()}\n{tool.ReturnJsonSchema?.GetRawText() ?? "(no return schema)"}";

    private static async Task<List<string>> ShapesAsync(IToolSource source) =>
        [.. (await source.GetToolsAsync(default)).Value.Cast<AIFunction>().OrderBy(t => t.Name, StringComparer.Ordinal).Select(Shape)];

    [Fact]
    public async Task The_host_serves_the_same_sandbox_and_workspace_tools_as_the_reflective_source()
    {
        await using var host = await HostHarness.StartAsync(WorkRoot());
        var registered = host.Services.GetServices<IToolSource>().ToList();

        foreach (var (name, type) in new[] { (SandboxToolOptions.SourceName, typeof(SandboxTools)), (RunWorkspaceToolOptions.SourceName, typeof(WorkspaceTools)) })
        {
            var expected = await ShapesAsync(new LocalToolSource(name, host.Services, [type]));

            // The registered source refuses an empty binder list, so this cannot pass on two empty lists.
            // Red: drop IsBoundByFactory from the shape guard: the host cannot build these tools and does not start.
            // Red: give LocalToolSource<TTools>'s functions the tool's name as their description: the lists differ.
            (await ShapesAsync(registered.Single(s => string.Equals(s.Name, name, StringComparison.Ordinal)))).Should().Equal(expected, name);
        }
    }

    [Fact]
    public async Task A_tool_source_that_cannot_build_its_tools_stops_the_start()
    {
        var start = () => HostHarness.StartAsync(
            WorkRoot(),
            services: s => s.AddThalos(t => t.AddLocalTools<BadBinderTools>("bad", JsonSerializerOptions.Default, _ => BadBinderTools.NotATool)));

        // Red: remove the ToolSourceWarmup registration from SandboxHost.AddTools: the host starts, and the bad binder
        // would fail each MCP request instead.
        await start.Should().ThrowAsync<InvalidOperationException>().WithMessage("*must return one of its [ThalosTool] methods*NotATool'.");
    }

    /// <summary>A tool source that answers a failure instead of throwing.</summary>
    private sealed class FailingToolSource : IToolSource
    {
        public string Name => "failing";

        public ValueTask<Result<IReadOnlyList<AITool>, AgentError>> GetToolsAsync(CancellationToken ct) =>
            new(Result<IReadOnlyList<AITool>, AgentError>.Failure(AgentError.Validation("the tools could not be listed")));
    }

    [Fact]
    public async Task A_tool_source_that_answers_a_failure_stops_the_start()
    {
        var start = () => HostHarness.StartAsync(WorkRoot(), services: s => s.AddSingleton<IToolSource>(new FailingToolSource()));

        // Red: make ToolSourceWarmup ignore a failed result: the host starts, and the route serves no tools instead.
        await start.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The sandbox host cannot start: tool source 'failing' failed: the tools could not be listed");
    }
}
