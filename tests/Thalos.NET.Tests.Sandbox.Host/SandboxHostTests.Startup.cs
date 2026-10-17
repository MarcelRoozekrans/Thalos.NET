using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Thalos.Sandbox;
using Thalos.Tools;
using Thalos.Workspaces;

namespace Thalos.Tests.Sandbox.Host;

public sealed partial class SandboxHostTests
{
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
}
