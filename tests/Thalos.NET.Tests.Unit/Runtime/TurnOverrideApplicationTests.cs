using Microsoft.Extensions.AI;
using Thalos.Runtime;
using Thalos.Testing;
using ZeroAlloc.Authorization;

namespace Thalos.Tests.Unit.Runtime;

/// <summary>
/// A validated model override and tool filter change what the provider is actually handed: these tests assert on the
/// <see cref="ChatOptions"/> the scripted model received, on the turn's usage and on the <c>thalos.turn</c> span, not on
/// an intermediate the runtime could fill in without the model ever seeing it.
/// </summary>
public sealed class TurnOverrideApplicationTests
{
    private static readonly ISecurityContext Caller = RuntimeFixture.User();
    private static readonly OutcomeToolSchema Approval = new("workflow__report_outcome", ["approved", "rejected"]);

    private static AIFunction Fn(string name) => AIFunctionFactory.Create(() => name, name);

    /// <summary>Two sources, so the agent's resolved tools are <c>a__one</c>, <c>a__two</c>, <c>b__one</c> in that order.</summary>
    private static RuntimeFixture TwoSources(params string[] allowTools) =>
        new RuntimeFixture(allowTools).WithSource("a", Fn("one"), Fn("two")).WithSource("b", Fn("one"));

    private static async Task<SessionId> SessionAsync(RuntimeFixture f) =>
        (await f.Runtime.CreateSessionAsync(f.Agent.Id, Caller, CancellationToken.None)).Value;

    private static IReadOnlyList<string> ToolsOf(ScriptedChatClient client, int request) =>
        [.. (client.Requests[request].Options?.Tools ?? []).Select(t => t.Name)];

    /// <summary>Red: <c>BuildRunOptions</c> stops setting <c>ModelId</c>, so the agent's own model m1 reaches the request (verified).</summary>
    [Fact]
    public async Task An_overridden_model_reaches_the_chat_request()
    {
        var f = new RuntimeFixture().WithAllowedModels("big-model").Build();
        f.Client.ThenText("done");
        var session = await SessionAsync(f);

        var result = await f.Runtime.RunTurnAsync(new AgentTurnRequest(session, "go", Caller) { Model = "big-model" }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        f.Client.Requests[0].Options!.ModelId.Should().Be("big-model");
    }

    /// <summary>
    /// The filter's patterns sort as <c>*two</c> before <c>a__one</c>, so walking the patterns instead of the resolved tools
    /// would offer a__two first. MAF puts the run's outcome tool ahead of the agent's own tools.
    /// Red: <c>BuildAsync</c> collects tools pattern by pattern, giving a__two before a__one (verified). Red: the filter
    /// is not applied, giving b__one too (verified).
    /// </summary>
    [Fact]
    public async Task A_tool_filter_offers_only_matching_tools_in_their_original_order()
    {
        var f = TwoSources().Build();
        f.Client.ThenText("done");
        var session = await SessionAsync(f);

        var result = await f.Runtime.RunTurnAsync(
            new AgentTurnRequest(session, "go", Caller) { ToolFilter = ["a__one", "*two"], RequiredOutcome = Approval }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        ToolsOf(f.Client, 0).Should().Equal("workflow__report_outcome", "a__one", "a__two");
    }

    /// <summary>
    /// The A2 security ruling: validation only proves each pattern matches one of the agent's tools, so the filter must
    /// narrow the agent's own resolved list and never be resolved against the host catalog. Red: <c>BuildAsync</c>
    /// resolves the catalog with the filter as the agent's tool globs, which offers the host's b__one as well (verified).
    /// </summary>
    [Fact]
    public async Task A_wildcard_filter_offers_exactly_the_agents_own_tools()
    {
        var f = TwoSources("a__*").Build();
        f.Client.ThenText("done");
        var session = await SessionAsync(f);

        var result = await f.Runtime.RunTurnAsync(new AgentTurnRequest(session, "go", Caller) { ToolFilter = ["*"] }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        ToolsOf(f.Client, 0).Should().Equal("a__one", "a__two");
    }

    /// <summary>
    /// Red: the cache key encodes the filter in request order without sorting, so the second turn builds a second
    /// pipeline (verified). Red: the filter is not applied, so the second turn also sees t__echo (verified).
    /// </summary>
    [Fact]
    public async Task Equal_filters_in_any_order_share_one_pipeline()
    {
        var builds = 0;
        var f = TwoSources();
        f.ChatClientFactory = _ => { Interlocked.Increment(ref builds); return f.Client; };
        f.WithTool(Fn("echo")).Build();
        f.Client.ThenText("one").ThenText("two");
        var session = await SessionAsync(f);

        var first = await f.Runtime.RunTurnAsync(new AgentTurnRequest(session, "go", Caller) { ToolFilter = ["b__*", "a__*"] }, CancellationToken.None);
        var second = await f.Runtime.RunTurnAsync(new AgentTurnRequest(session, "again", Caller) { ToolFilter = ["a__*", "b__*"] }, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        builds.Should().Be(1);
        ToolsOf(f.Client, 1).Should().Equal("a__one", "a__two", "b__one");
    }

    /// <summary>
    /// The filter is part of the cache key. Red: dropping it from <c>CacheKey</c> lets the unfiltered turn reuse the
    /// filtered pipeline and see only a__one (verified). Red: the filter is not applied, so the first turn sees every
    /// tool (verified).
    /// </summary>
    [Fact]
    public async Task An_unfiltered_turn_after_a_filtered_one_offers_every_tool()
    {
        var f = TwoSources().Build();
        f.Client.ThenText("one").ThenText("two");
        var session = await SessionAsync(f);

        await f.Runtime.RunTurnAsync(new AgentTurnRequest(session, "go", Caller) { ToolFilter = ["a__one"] }, CancellationToken.None);
        var unfiltered = await f.Runtime.RunTurnAsync(new AgentTurnRequest(session, "again", Caller), CancellationToken.None);

        unfiltered.IsSuccess.Should().BeTrue();
        ToolsOf(f.Client, 0).Should().Equal("a__one");
        ToolsOf(f.Client, 1).Should().Equal("a__one", "a__two", "b__one");
    }

    /// <summary>
    /// Validation and the pipeline build share one resolution of the agent's tools. Red: <c>ValidateOverridesAsync</c>
    /// calls the catalog directly again, or the resolution cache is bypassed, so a cold filtered turn resolves twice
    /// (both verified).
    /// </summary>
    [Fact]
    public async Task A_filtered_turn_resolves_the_agents_tools_once()
    {
        var f = TwoSources().Build();
        f.Client.ThenText("done");
        var session = await SessionAsync(f);

        var result = await f.Runtime.RunTurnAsync(new AgentTurnRequest(session, "go", Caller) { ToolFilter = ["a__*"] }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        f.ToolResolutions.Should().Be(1);
    }

    /// <summary>
    /// The scripted model reports scripted-model and the agent pins m1, so only the override can produce big-model.
    /// Red: <c>TurnUsage.Empty</c> starts from <c>definition.Model</c> again, giving m1 (verified).
    /// </summary>
    [Fact]
    public async Task Usage_records_the_effective_model()
    {
        var f = new RuntimeFixture().WithAllowedModels("big-model").Build();
        f.Client.ThenText("done");
        var session = await SessionAsync(f);

        var result = await f.Runtime.RunTurnAsync(new AgentTurnRequest(session, "go", Caller) { Model = "big-model" }, CancellationToken.None);

        result.Value.Usage.ModelId.Should().Be("big-model");
    }

    /// <summary>
    /// Red: the model tag is not set, or is set from <c>definition.Model</c>, giving m1. Red: the count tag is not set,
    /// or the filter is not applied, giving 4, or the outcome tool is left out, giving 2, or only the run's tools are
    /// counted, giving 1. Red: a tag joins the offered tool names, which the last assertion catches. All verified.
    /// </summary>
    [Fact]
    public async Task The_turn_span_carries_the_effective_model_and_the_offered_tool_count()
    {
        using var capture = new TelemetryCapture(ThalosTelemetry.SourceName);
        var f = TwoSources().WithAllowedModels("big-model").Build();
        f.Client.ThenText("done");
        var session = await SessionAsync(f);

        await f.Runtime.RunTurnAsync(
            new AgentTurnRequest(session, "go", Caller) { Model = "big-model", ToolFilter = ["a__*"], RequiredOutcome = Approval },
            CancellationToken.None);

        var span = capture.SpansNamed("thalos.turn").Single(s => Equals(s.GetTagItem("thalos.session"), session.ToString()));
        span.GetTagItem("gen_ai.request.model").Should().Be("big-model");
        span.GetTagItem("thalos.tools.offered").Should().Be(3);
        string[] toolNames = ["a__one", "a__two", "b__one", "workflow__report_outcome"];
        span.TagObjects.Select(t => t.Value?.ToString() ?? string.Empty)
            .Should().NotContain(v => toolNames.Any(n => v.Contains(n, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Without an override the span names the model the agent runs on, here the provider default because the agent
    /// pins none. Red: the tag is taken from the request and <c>definition.Model</c> alone, or is not set, and is then
    /// missing. Red: the count is not set, or counts only the run's tools, giving 0. All verified.
    /// </summary>
    [Fact]
    public async Task The_turn_span_names_the_agents_own_model_without_an_override()
    {
        using var capture = new TelemetryCapture(ThalosTelemetry.SourceName);
        var f = new RuntimeFixture().WithModel(null).WithTool(Fn("echo")).Build();
        f.Client.ThenText("done");
        var session = await SessionAsync(f);

        await f.Runtime.RunTurnAsync(new AgentTurnRequest(session, "go", Caller), CancellationToken.None);

        var span = capture.SpansNamed("thalos.turn").Single(s => Equals(s.GetTagItem("thalos.session"), session.ToString()));
        span.GetTagItem("gen_ai.request.model").Should().Be("dm");
        span.GetTagItem("thalos.tools.offered").Should().Be(1);
    }
}
