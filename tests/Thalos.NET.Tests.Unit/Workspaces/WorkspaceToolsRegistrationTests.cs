using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Thalos.Tools;
using Thalos.Workspaces;

namespace Thalos.Tests.Unit.Workspaces;

/// <summary>
/// <c>UseRunWorkspaceTools</c> registers the workspace tools through the reflection-free <c>AddLocalTools&lt;WorkspaceTools&gt;</c>.
/// What the model sees must be what the reflective <see cref="LocalToolSource"/> over <see cref="WorkspaceTools"/>
/// offered before: the same names, descriptions, argument schemas and result schemas.
/// </summary>
public sealed class WorkspaceToolsRegistrationTests
{
    /// <summary>One tool as the model sees it.</summary>
    internal static string Shape(AIFunction tool) =>
        $"{tool.Name}\n{tool.Description}\n{tool.JsonSchema.GetRawText()}\n{tool.ReturnJsonSchema?.GetRawText() ?? "(no return schema)"}";

    /// <summary>The source's tools, as <see cref="Shape"/>s ordered by name.</summary>
    internal static async Task<List<string>> ShapesAsync(IToolSource source) =>
        [.. (await source.GetToolsAsync(default)).Value.Cast<AIFunction>().OrderBy(t => t.Name, StringComparer.Ordinal).Select(Shape)];

    [Fact]
    public async Task The_registered_tools_match_the_reflective_source_over_WorkspaceTools()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IRunWorkspaceProvider>());
        services.AddThalos(t => t.UseRunWorkspaceTools(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" }));
        await using var provider = services.BuildServiceProvider();
        var registered = provider.GetServices<IToolSource>().Single(s => string.Equals(s.Name, RunWorkspaceToolOptions.SourceName, StringComparison.Ordinal));
        var reflective = new LocalToolSource(RunWorkspaceToolOptions.SourceName, provider, [typeof(WorkspaceTools)]);

        var expected = await ShapesAsync(reflective);

        // The registered source refuses an empty binder list, so this cannot pass on two empty lists.
        // Red: drop IsBoundByFactory from the shape guard: the registered source cannot build its tools, because its
        // source-generated options have no metadata for CancellationToken.
        // Red: give LocalToolSource<TTools>'s functions the tool's name as their description: the lists differ.
        (await ShapesAsync(registered)).Should().Equal(expected);
    }
}
