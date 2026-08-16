using Microsoft.Extensions.AI;
using NSubstitute;
using Thalos.Tools;
using ZeroAlloc.Results;

namespace Thalos.Tests.Unit.Tools;

public sealed class ToolCatalogTests
{
    private static IToolSource Source(string name, params string[] tools)
    {
        var s = Substitute.For<IToolSource>();
        s.Name.Returns(name);
        IReadOnlyList<AITool> list = tools.Select(t => (AITool)AIFunctionFactory.Create(() => t, t)).ToList();
        s.GetToolsAsync(Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<AITool>, AgentError>.Success(list));
        return s;
    }

    private static AgentDefinition Agent(params string[] allow) => new()
    {
        Id = AgentId.New(), Name = "a", Instructions = "i", Tools = allow.Length == 0 ? ["*"] : allow,
    };

    private static ToolCatalog Catalog(params IToolSource[] sources) =>
        new(sources, Substitute.For<IToolAuthorizer>(), new RecordingPublisher(), TimeProvider.System);

    [Fact]
    public async Task Qualifies_names_with_source_prefix_and_wraps_in_AuthorizingAIFunction()
    {
        var catalog = Catalog(Source("roslyn", "find_callers"), Source("mem", "snapshot"));
        var tools = (await catalog.ResolveAsync(Agent(), default)).Value;

        tools.Select(t => t.Name).Should().BeEquivalentTo(["roslyn__find_callers", "mem__snapshot"]);
        tools.Should().AllBeOfType<AuthorizingAIFunction>();
    }

    [Fact]
    public async Task Applies_agent_allow_list_globs()
    {
        var catalog = Catalog(Source("roslyn", "find_callers", "apply_code_action"), Source("mem", "snapshot"));
        var tools = (await catalog.ResolveAsync(Agent("roslyn__find_*", "mem__*"), default)).Value;
        tools.Select(t => t.Name).Should().BeEquivalentTo(["roslyn__find_callers", "mem__snapshot"]);
    }

    [Fact]
    public async Task Failing_source_is_skipped_not_fatal()
    {
        var bad = Substitute.For<IToolSource>();
        bad.Name.Returns("bad");
        bad.GetToolsAsync(Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<AITool>, AgentError>.Failure(AgentError.ProviderError("down")));

        var catalog = Catalog(bad, Source("ok", "t"));
        var r = await catalog.ResolveAsync(Agent(), default);
        r.IsSuccess.Should().BeTrue();
        r.Value.Select(t => t.Name).Should().Equal("ok__t");
    }

    [Fact]
    public async Task Duplicate_qualified_names_keep_first_and_are_reported()
    {
        var catalog = Catalog(Source("x", "t"), Source("x", "t"));
        var r = await catalog.ResolveAsync(Agent(), default);
        r.Value.Should().HaveCount(1);
    }
}
