using Thalos.Memory;
using Thalos.Runtime;
using ZeroAlloc.Authorization;

namespace Thalos.Tests.Memory;

public sealed class MemoryServiceRememberTests
{
    [Fact]
    public async Task Remember_stores_indexes_and_publishes_MemoryStored_on_the_hub_outside_a_turn()
    {
        var f = new MemoryServiceFixture();
        var svc = f.Build();

        var r = await svc.RememberAsync(MemoryServiceFixture.Remember("The user prefers xUnit.", tags: [" testing ", "testing", "prefs"]), default);

        r.IsSuccess.Should().BeTrue(r.IsFailure ? r.Error.ToString() : "");
        r.Value.IndexPending.Should().BeFalse();
        r.Value.Tags.Should().Equal("testing", "prefs");
        r.Value.CreatedAt.Should().Be(f.Clock.GetUtcNow());
        (await f.Store.GetAsync(r.Value.Id, default)).Value.IndexPending.Should().BeFalse();
        (await f.Index.SearchAsync("xUnit", new MemoryScope("alice", null), new MemorySearchOptions(5, 0.1), default)).Value.Should().ContainSingle(h => h.Id == r.Value.Id);
        f.HubEvents.Should().ContainSingle().Which.Should().BeOfType<MemoryStoredEvent>().Which.Deduped.Should().BeFalse();
    }

    [Fact]
    public async Task Remember_inside_a_turn_publishes_into_the_turn_scope()
    {
        var f = new MemoryServiceFixture();
        var svc = f.Build();
        var s = SessionId.New(); var t = TurnId.New();
        using var scope = TurnScope.Begin(s, t, new TestCaller("alice"), AgentId.New());

        await svc.RememberAsync(MemoryServiceFixture.Remember("x y z"), default);

        scope.Events.TryRead(out var evt).Should().BeTrue();
        evt.Should().BeOfType<MemoryStoredEvent>().Which.SessionId.Should().Be(s);
        f.HubEvents.Should().BeEmpty("the runtime forwards scope events to the hub; the service must not double-publish");
    }

    [Fact]
    public async Task Invalid_request_returns_MemoryValidationFailed_and_stores_nothing()
    {
        var f = new MemoryServiceFixture();
        var svc = f.Build();
        var r = await svc.RememberAsync(MemoryServiceFixture.Remember("   "), default);
        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be(AgentErrorCode.MemoryValidationFailed);
        (await f.Store.ListAsync(new MemoryQuery(), default)).Value.TotalCount.Should().Be(0);
        (await svc.RememberAsync(MemoryServiceFixture.Remember("ok", importance: 2), default)).Error.Code.Should().Be(AgentErrorCode.MemoryValidationFailed);
    }

    [Fact]
    public async Task Blank_and_anonymous_owners_are_rejected_and_store_nothing()
    {
        var f = new MemoryServiceFixture();
        var svc = f.Build();

        (await svc.RememberAsync(MemoryServiceFixture.Remember("ok", owner: "   "), default)).Error.Code.Should().Be(AgentErrorCode.MemoryValidationFailed);
        (await svc.RememberAsync(MemoryServiceFixture.Remember("ok", owner: ""), default)).Error.Code.Should().Be(AgentErrorCode.MemoryValidationFailed);
        (await svc.RememberAsync(MemoryServiceFixture.Remember("ok", owner: AnonymousSecurityContext.AnonymousId), default)).Error.Code.Should().Be(AgentErrorCode.MemoryValidationFailed);
        (await f.Store.ListAsync(new MemoryQuery(), default)).Value.TotalCount.Should().Be(0);
        f.HubEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Index_failure_keeps_the_record_pending_and_reports_success()
    {
        var f = new MemoryServiceFixture(UnavailableMemoryIndex.Instance);
        var svc = f.Build();

        var r = await svc.RememberAsync(MemoryServiceFixture.Remember("stored but not searchable"), default);

        r.IsSuccess.Should().BeTrue();
        r.Value.IndexPending.Should().BeTrue();
        (await f.Store.GetAsync(r.Value.Id, default)).Value.IndexPending.Should().BeTrue();
        f.HubEvents.Should().ContainSingle().Which.Should().BeOfType<MemoryIndexPendingEvent>();
    }
}
