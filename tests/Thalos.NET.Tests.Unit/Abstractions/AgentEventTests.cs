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
    [InlineData(typeof(MemoryRecalledEvent), "memory-recalled")]
    [InlineData(typeof(MemoryStoredEvent), "memory-stored")]
    [InlineData(typeof(MemoryRecallFailedEvent), "memory-recall-failed")]
    [InlineData(typeof(MemoryIndexPendingEvent), "memory-index-pending")]
    [InlineData(typeof(MemoryQuarantinedEvent), "memory-quarantined")]
    [InlineData(typeof(SkillCatalogueFailedEvent), "skill-catalogue-failed")]
    public void Kinds_are_stable_wire_names(Type type, string kind)
    {
        AgentEvent.KindOf(type).Should().Be(kind);
    }

    public static TheoryData<AgentEvent> AllEvents()
    {
        var s = SessionId.New();
        var t = TurnId.New();
        var c = ToolCallId.New();
        return new TheoryData<AgentEvent>
        {
            new TextDeltaEvent(s, t, "hi"),
            new ToolCallStartedEvent(s, t, c, "tool", "{}"),
            new ToolCallFinishedEvent(s, t, c, "tool", true, null, TimeSpan.Zero),
            new UsageEvent(s, t, TurnUsage.Empty("m")),
            new TurnCompletedEvent(s, t, new AgentTurnResult(t, s, "", TurnUsage.Empty("m"), [], TimeSpan.Zero)),
            new TurnFailedEvent(s, t, AgentError.Cancelled()),
        };
    }

    [Theory]
    [MemberData(nameof(AllEvents))]
    public void Instance_kind_matches_KindOf_its_type(AgentEvent e)
    {
        e.Kind.Should().Be(AgentEvent.KindOf(e.GetType()));
    }

    [Fact]
    public void KindOf_rejects_non_event_types()
    {
        var act = () => AgentEvent.KindOf(typeof(string));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Request_requires_text()
    {
        var r = new AgentTurnRequest(SessionId.New(), "", AnonymousSecurityContext.Instance);
        new AgentTurnRequestValidator().Validate(r).IsValid.Should().BeFalse();
    }
}
