using Microsoft.Extensions.AI;
using NSubstitute;
using ZeroAlloc.Authorization;

namespace Thalos.Tests.Unit.Runtime;

public sealed class ThalosAgentRuntimeStreamingTests
{
    [Fact]
    public async Task Text_turn_streams_deltas_then_usage_then_done()
    {
        var f = new RuntimeFixture().Build();
        f.Client.ThenText("hello streaming world", input: 5, output: 3);
        var s = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User(), default)).Value;

        var events = await f.Runtime.RunTurnStreamingAsync(new AgentTurnRequest(s, "hi", RuntimeFixture.User()), default).ToListAsync();

        events.Select(e => e.Kind).Should().Equal("text-delta", "text-delta", "text-delta", "usage", "done");
        string.Concat(events.OfType<TextDeltaEvent>().Select(e => e.Text)).Should().Be("hello streaming world");
        events.OfType<UsageEvent>().Single().Usage.Should().Be(new TurnUsage(5, 3, "m1"));
        events.OfType<TurnCompletedEvent>().Single().Result.Text.Should().Be("hello streaming world");
    }

    [Fact]
    public async Task Tool_turn_streams_tool_call_and_result_before_final_text()
    {
        var f = new RuntimeFixture().WithTool(AIFunctionFactory.Create((string text) => "echo:" + text, "echo")).Build();
        f.Client.ThenToolCall("t__echo", new { text = "x" }).ThenText("done");
        var s = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User(), default)).Value;

        var kinds = (await f.Runtime.RunTurnStreamingAsync(new AgentTurnRequest(s, "go", RuntimeFixture.User()), default).ToListAsync()).Select(e => e.Kind).ToList();

        kinds.Should().ContainInOrder("tool-call", "tool-result", "text-delta", "usage", "done");
        kinds[^1].Should().Be("done");
    }

    [Fact]
    public async Task Failure_streams_single_error_event()
    {
        var f = new RuntimeFixture().Build();
        f.Client.ThenThrow(new HttpRequestException("x"));
        var s = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User(), default)).Value;

        var events = await f.Runtime.RunTurnStreamingAsync(new AgentTurnRequest(s, "hi", RuntimeFixture.User()), default).ToListAsync();
        events.Should().ContainSingle().Which.Should().BeOfType<TurnFailedEvent>();
    }

    [Fact]
    public async Task Hub_subscribers_see_every_event()
    {
        var f = new RuntimeFixture().Build();
        f.Client.ThenText("a b");
        var seen = new List<string>();
        using var _ = f.Hub.Subscribe((e, ct) => { lock (seen) seen.Add(e.Kind); return default; });
        var s = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User(), default)).Value;

        await f.Runtime.RunTurnAsync(new AgentTurnRequest(s, "hi", RuntimeFixture.User()), default);

        seen.Should().Equal("text-delta", "text-delta", "usage", "done");
    }

    /// <summary>
    /// Regression guard for the producer/drain design: text deltas are yielded to the consumer *before* the tool runs,
    /// and the tool must still see the real caller (an AsyncLocal scope living in the iterator would be lost here).
    /// </summary>
    [Fact]
    public async Task Tool_called_after_streamed_text_still_sees_the_caller()
    {
        var f = new RuntimeFixture().WithTool(AIFunctionFactory.Create((string text) => "echo:" + text, "echo")).Build();
        // one model response containing text AND a tool call (Anthropic does this), then the final answer
        f.Client.ThenToolCall("t__echo", new { text = "x" }, precedingText: "Let me check that.").ThenText("done");
        var s = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User("alice"), default)).Value;

        var events = await f.Runtime.RunTurnStreamingAsync(new AgentTurnRequest(s, "go", RuntimeFixture.User("alice")), default).ToListAsync();

        events.Select(e => e.Kind).Should().ContainInOrder("text-delta", "tool-call", "tool-result", "text-delta", "usage", "done");
        await f.Authorizer.Received(1).AuthorizeAsync(Arg.Is<ISecurityContext>(c => c.Id == "alice"), "t__echo", Arg.Any<System.Text.Json.JsonElement>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The consumer walks away mid-turn (client disconnect): the linked token cancels the model loop, the session is
    /// released, and the terminal error event still reaches hub subscribers even though the enumeration can no longer receive it.
    /// </summary>
    [Fact]
    public async Task Abandoned_enumeration_cancels_the_turn_releases_the_session_and_ends_on_the_hub_with_an_error()
    {
        var hang = AIFunctionFactory.Create(async (CancellationToken ct) => { await Task.Delay(Timeout.Infinite, ct); return "never"; }, "hang");
        var f = new RuntimeFixture().WithTool(hang).Build();
        f.Client.ThenToolCall("t__hang", new { }, precedingText: "Working on it.").ThenText("unreachable");
        var seen = new List<string>();
        using var _ = f.Hub.Subscribe((e, ct) => { lock (seen) seen.Add(e.Kind); return default; });
        var s = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User(), default)).Value;

        await foreach (var e in f.Runtime.RunTurnStreamingAsync(new AgentTurnRequest(s, "go", RuntimeFixture.User()), default))
        {
            e.Kind.Should().Be("text-delta");
            break; // consumer abandons the stream after the first delta
        }

        (await f.Store.GetAsync(s, default)).Value.State.Should().Be(SessionState.Idle);
        f.Publisher.Of<TurnFailedNotification>().Should().ContainSingle(n => n.Error.Code == AgentErrorCode.Cancelled);
        f.Publisher.Of<TurnCompletedNotification>().Should().BeEmpty();
        seen.Should().StartWith("text-delta");
        seen[^1].Should().Be("error");
        seen.Should().ContainSingle(k => string.Equals(k, "error", StringComparison.Ordinal));
    }
}
