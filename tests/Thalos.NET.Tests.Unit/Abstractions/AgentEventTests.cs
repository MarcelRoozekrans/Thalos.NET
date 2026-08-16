using ZeroAlloc.Authorization;

namespace Thalos.Tests.Unit.Abstractions;

public sealed class AgentEventTests
{
    [Fact]
    public void Events_carry_session_and_turn()
    {
        var s = SessionId.New(); var t = TurnId.New();
        AgentEvent e = new TextDeltaEvent(s, t, "hi");
        e.SessionId.Should().Be(s);
        e.TurnId.Should().Be(t);
        e.Kind.Should().Be("text-delta");
    }

    [Theory]
    [InlineData(typeof(TextDeltaEvent), "text-delta")]
    [InlineData(typeof(ToolCallStartedEvent), "tool-call")]
    [InlineData(typeof(ToolCallFinishedEvent), "tool-result")]
    [InlineData(typeof(UsageEvent), "usage")]
    [InlineData(typeof(TurnCompletedEvent), "done")]
    [InlineData(typeof(TurnFailedEvent), "error")]
    public void Kinds_are_stable_wire_names(Type type, string kind)
    {
        AgentEvent.KindOf(type).Should().Be(kind);
    }

    [Fact]
    public void Request_requires_text()
    {
        var r = new AgentTurnRequest(SessionId.New(), "", AnonymousSecurityContext.Instance);
        new AgentTurnRequestValidator().Validate(r).IsValid.Should().BeFalse();
    }
}
