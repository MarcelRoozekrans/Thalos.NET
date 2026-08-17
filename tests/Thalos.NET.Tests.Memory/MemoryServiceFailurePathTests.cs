using Microsoft.Extensions.Logging;
using Thalos.Memory;
using Thalos.Runtime;

namespace Thalos.Tests.Memory;

/// <summary>Degraded paths of <see cref="MemoryService"/>: store hiccups are logged (event ids 501–506) and never lose a committed record.</summary>
public sealed class MemoryServiceFailurePathTests
{
    private static readonly AgentError StoreDown = AgentError.MemoryStoreFailed("store down", "Test");

    [Fact]
    public async Task Clear_pending_failure_on_remember_is_logged_and_the_record_stays_pending_but_stored()
    {
        var f = new MemoryServiceFixture();
        var store = new HookedStore(f.Store) { OnUpdate = (_, u) => u.IndexPending == false ? StoreDown : null };
        var svc = f.Build(store);

        var r = await svc.RememberAsync(MemoryServiceFixture.Remember("golf hotel"), default);

        r.IsSuccess.Should().BeTrue();
        r.Value.IndexPending.Should().BeTrue("the flag could not be cleared");
        (await f.Index.SearchAsync("golf hotel", new MemoryScope("alice", null), new MemorySearchOptions(5, 0.1), default)).Value.Should().ContainSingle("the vector was written");
        f.HubEvents.Should().ContainSingle().Which.Should().BeOfType<MemoryStoredEvent>();
        f.Logger.Entries.Should().Contain((505, LogLevel.Warning));
    }

    [Fact]
    public async Task Reindex_counts_records_whose_pending_flag_could_not_be_cleared_as_failed()
    {
        var f = new MemoryServiceFixture(UnavailableMemoryIndex.Instance);
        var svc = f.Build();
        var a = (await svc.RememberAsync(MemoryServiceFixture.Remember("india juliet"), default)).Value;
        await svc.RememberAsync(MemoryServiceFixture.Remember("kilo lima mike"), default);

        f.Index = new InMemoryMemoryIndex(new Thalos.Testing.HashedBagOfWordsEmbeddingGenerator());
        var store = new HookedStore(f.Store) { OnUpdate = (id, u) => id == a.Id && u.IndexPending == false ? StoreDown : null };
        var report = (await f.Build(store).ReindexAsync(new ReindexOptions(), default)).Value;

        report.Should().Be(new ReindexReport(Scanned: 2, Indexed: 1, Failed: 1));
        (await f.Store.GetAsync(a.Id, default)).Value.IndexPending.Should().BeTrue("it is re-embedded next run");
        f.Logger.Entries.Should().Contain((505, LogLevel.Warning));

        (await f.Build().ReindexAsync(new ReindexOptions(), default)).Value.Should().Be(new ReindexReport(Scanned: 1, Indexed: 1, Failed: 0));
    }

    [Fact]
    public async Task Dedupe_refresh_failure_falls_through_to_insert()
    {
        var f = new MemoryServiceFixture();
        var store = new HookedStore(f.Store) { OnUpdate = (_, u) => u.Importance is not null ? StoreDown : null };
        var svc = f.Build(store);
        var first = (await svc.RememberAsync(MemoryServiceFixture.Remember("november oscar"), default)).Value;

        var again = await svc.RememberAsync(MemoryServiceFixture.Remember("november oscar", importance: 0.9), default);

        again.IsSuccess.Should().BeTrue();
        again.Value.Id.Should().NotBe(first.Id);
        (await f.Store.ListAsync(new MemoryQuery { OwnerIds = ["alice"] }, default)).Value.TotalCount.Should().Be(2);
        f.HubEvents.OfType<MemoryStoredEvent>().Last().Deduped.Should().BeFalse();
        f.Logger.Entries.Should().Contain((501, LogLevel.Warning));
    }

    [Fact]
    public async Task MarkRecalled_failure_is_logged_and_recall_still_returns()
    {
        var f = new MemoryServiceFixture();
        var store = new HookedStore(f.Store) { OnMarkRecalled = () => StoreDown };
        var svc = f.Build(store);
        var m = (await svc.RememberAsync(MemoryServiceFixture.Remember("papa quebec"), default)).Value;

        var r = await svc.RecallAsync("papa quebec", new MemoryScope("alice", null), new RecallOptions { MinScore = 0.1 }, default);

        r.IsSuccess.Should().BeTrue();
        r.Value.Should().ContainSingle(x => x.Record.Id == m.Id);
        (await f.Store.GetAsync(m.Id, default)).Value.RecallCount.Should().Be(0);
        f.Logger.Entries.Should().Contain((502, LogLevel.Warning));
    }

    [Fact]
    public async Task Hydration_store_error_other_than_not_found_fails_the_recall()
    {
        var f = new MemoryServiceFixture();
        var svc = f.Build();
        await svc.RememberAsync(MemoryServiceFixture.Remember("romeo sierra"), default);
        var store = new HookedStore(f.Store) { OnGet = _ => StoreDown };

        var r = await f.Build(store).RecallAsync("romeo sierra", new MemoryScope("alice", null), new RecallOptions { MinScore = 0.1 }, default);

        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be(AgentErrorCode.MemoryStoreFailed);
    }

    [Fact]
    public async Task MaxChars_zero_or_negative_means_no_char_budget()
    {
        var f = new MemoryServiceFixture();
        f.Options.Dedupe.Enabled = false;
        var svc = f.Build();
        await svc.RememberAsync(MemoryServiceFixture.Remember("tango uniform " + new string('x', 500)), default);
        await svc.RememberAsync(MemoryServiceFixture.Remember("tango uniform " + new string('y', 500)), default);

        (await svc.RecallAsync("tango uniform", new MemoryScope("alice", null), new RecallOptions { MinScore = 0.1, MaxChars = 0 }, default)).Value.Should().HaveCount(2);
        (await svc.RecallAsync("tango uniform", new MemoryScope("alice", null), new RecallOptions { MinScore = 0.1, MaxChars = -1 }, default)).Value.Should().HaveCount(2);
        (await svc.RecallAsync("tango uniform", new MemoryScope("alice", null), new RecallOptions { MinScore = 0.1, MaxChars = 600 }, default)).Value.Should().HaveCount(1);
    }

    [Fact]
    public async Task Hard_forget_then_soft_forget_reports_not_found()
    {
        var f = new MemoryServiceFixture();
        var svc = f.Build();
        var m = (await svc.RememberAsync(MemoryServiceFixture.Remember("victor whiskey"), default)).Value;

        (await svc.ForgetAsync(m.Id, new MemoryScope("alice", null), hard: true, default)).IsSuccess.Should().BeTrue();
        (await svc.ForgetAsync(m.Id, new MemoryScope("alice", null), hard: false, default)).Error.Code.Should().Be(AgentErrorCode.MemoryNotFound);
    }

    [Fact]
    public async Task Index_pending_event_inside_a_turn_goes_to_the_scope()
    {
        var f = new MemoryServiceFixture(UnavailableMemoryIndex.Instance);
        var svc = f.Build();
        var s = SessionId.New(); var t = TurnId.New();
        using var scope = TurnScope.Begin(s, t, new TestCaller("alice"), AgentId.New());

        var r = await svc.RememberAsync(MemoryServiceFixture.Remember("xray yankee"), default);

        r.Value.IndexPending.Should().BeTrue();
        scope.Events.TryRead(out var evt).Should().BeTrue();
        evt.Should().BeOfType<MemoryIndexPendingEvent>().Which.Should().BeEquivalentTo(new { SessionId = s, TurnId = t, MemoryId = r.Value.Id });
        f.HubEvents.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public async Task Invalid_dedupe_threshold_disables_dedupe_and_warns_once(double threshold)
    {
        var f = new MemoryServiceFixture();
        f.Options.Dedupe.Threshold = threshold;
        var svc = f.Build();

        await svc.RememberAsync(MemoryServiceFixture.Remember("zulu alpha"), default);
        await svc.RememberAsync(MemoryServiceFixture.Remember("zulu alpha"), default);
        await svc.RememberAsync(MemoryServiceFixture.Remember("zulu alpha"), default);

        (await f.Store.ListAsync(new MemoryQuery { OwnerIds = ["alice"] }, default)).Value.TotalCount.Should().Be(3, "identical texts insert when dedupe is off");
        f.Logger.Entries.Where(e => e.EventId == 506).Should().ContainSingle();
    }
}
