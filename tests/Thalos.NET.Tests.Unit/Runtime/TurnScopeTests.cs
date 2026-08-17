using Thalos.Runtime;
using ZeroAlloc.Authorization;

namespace Thalos.Tests.Unit.Runtime;

public sealed class TurnScopeTests
{
    [Fact]
    public void Current_is_null_outside_a_scope()
    {
        TurnScope.Current.Should().BeNull();
    }

    [Fact]
    public async Task Scope_flows_across_awaits_and_is_restored_on_dispose()
    {
        var caller = new TestSecurityContext("u1", "developer");
        var s = SessionId.New(); var t = TurnId.New();

        using (var scope = TurnScope.Begin(s, t, caller))
        {
            await Task.Yield();
            TurnScope.Current.Should().BeSameAs(scope);
            scope.SessionId.Should().Be(s);
            scope.Caller.Id.Should().Be("u1");
        }

        TurnScope.Current.Should().BeNull();
    }

    [Fact]
    public void Nested_scopes_are_LIFO_and_disposing_the_inner_restores_the_outer()
    {
        using var outer = TurnScope.Begin(SessionId.New(), TurnId.New(), AnonymousSecurityContext.Instance);

        using (var inner = TurnScope.Begin(SessionId.New(), TurnId.New(), AnonymousSecurityContext.Instance))
        {
            TurnScope.Current.Should().BeSameAs(inner);
        }

        TurnScope.Current.Should().BeSameAs(outer);
    }

    [Fact]
    public async Task Tool_events_are_queued_and_summaries_collected()
    {
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), AnonymousSecurityContext.Instance);
        var call = ToolCallId.New();

        await scope.PublishAsync(new ToolCallStartedEvent(scope.SessionId, scope.TurnId, call, "x__y", "{}"), CancellationToken.None);
        scope.RecordToolCall(new ToolCallSummary(call, "x__y", "{}", true, "ok", TimeSpan.Zero));

        scope.Events.TryRead(out var e).Should().BeTrue();
        e.Should().BeOfType<ToolCallStartedEvent>();
        scope.ToolCalls.Should().ContainSingle(c => c.Id == call);
    }

    [Fact]
    public async Task Publish_after_dispose_does_not_throw_and_the_channel_is_completed()
    {
        var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), AnonymousSecurityContext.Instance);
        scope.Dispose();

        var act = () => scope.PublishAsync(new TextDeltaEvent(scope.SessionId, scope.TurnId, "late"), CancellationToken.None).AsTask();

        await act.Should().NotThrowAsync();
        scope.Events.Completion.IsCompleted.Should().BeTrue();
        scope.Events.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task Scope_does_not_survive_yield_return_in_an_async_iterator()
    {
        // Pins the behaviour the runtime design relies on: the AsyncLocal is reset on each resumption of an async
        // iterator, so the runtime must own the scope in a producer Task and drain events through the channel.
        TurnScope? captured = null;

        async IAsyncEnumerable<int> Produce()
        {
            using var s = TurnScope.Begin(SessionId.New(), TurnId.New(), AnonymousSecurityContext.Instance);
            yield return 1;
            captured = TurnScope.Current;
            yield return 2;
        }

        var items = new List<int>();
        await foreach (var i in Produce())
        {
            items.Add(i);
        }

        items.Should().Equal(1, 2);
        captured.Should().BeNull();
        TurnScope.Current.Should().BeNull();
    }

    [Fact]
    public void Begin_carries_the_agent_id_and_defaults_to_none()
    {
        var agent = AgentId.New();
        using (var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), AnonymousSecurityContext.Instance, agent))
        {
            scope.AgentId.Should().Be(agent);
        }

        using var legacy = TurnScope.Begin(SessionId.New(), TurnId.New(), AnonymousSecurityContext.Instance);
        legacy.AgentId.Should().Be(default(AgentId));
    }

    [Fact]
    public async Task Extensions_can_publish_events_into_the_turn()
    {
        var s = SessionId.New(); var t = TurnId.New();
        using var scope = TurnScope.Begin(s, t, AnonymousSecurityContext.Instance);
        await scope.PublishAsync(new MemoryIndexPendingEvent(s, t, MemoryId.New()), CancellationToken.None);
        scope.Events.TryRead(out var evt).Should().BeTrue();
        evt.Should().BeOfType<MemoryIndexPendingEvent>();
    }
}
