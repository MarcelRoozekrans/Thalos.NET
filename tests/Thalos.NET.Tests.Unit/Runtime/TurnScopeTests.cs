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
    public async Task Tool_events_are_queued_and_summaries_collected()
    {
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), AnonymousSecurityContext.Instance);
        var call = ToolCallId.New();

        await scope.PublishAsync(new ToolCallStartedEvent(scope.SessionId, scope.TurnId, call, "x__y", "{}"), CancellationToken.None);
        scope.RecordToolCall(new ToolCallSummary(call, "x__y", "{}", true, "ok", TimeSpan.Zero));

        scope.Events.Reader.TryRead(out var e).Should().BeTrue();
        e.Should().BeOfType<ToolCallStartedEvent>();
        scope.ToolCalls.Should().ContainSingle(c => c.Id == call);
    }
}
