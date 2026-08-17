using Thalos.Memory;

namespace Thalos.Tests.Memory;

public sealed class MemoryServiceRecallTests
{
    private static RecallOptions Opts(int topK = 5, double minScore = 0.1, int maxChars = 2000) => new() { TopK = topK, MinScore = minScore, MaxChars = maxChars };

    [Fact]
    public async Task Recall_hydrates_orders_and_marks_recalled()
    {
        var f = new MemoryServiceFixture();
        var svc = f.Build();
        var exact = (await svc.RememberAsync(MemoryServiceFixture.Remember("deploy the api with blue green releases", importance: 0.3), default)).Value;
        var partial = (await svc.RememberAsync(MemoryServiceFixture.Remember("deploy the web app on fridays", importance: 0.9), default)).Value;
        await svc.RememberAsync(MemoryServiceFixture.Remember("unrelated playwright locators"), default);
        f.Clock.Advance(TimeSpan.FromMinutes(1));

        var r = await svc.RecallAsync("deploy the api with blue green releases", new MemoryScope("alice", null), Opts(), default);

        r.IsSuccess.Should().BeTrue();
        r.Value.Select(m => m.Record.Id).Should().Equal(exact.Id, partial.Id);
        r.Value[0].Score.Should().BeGreaterThan(r.Value[1].Score);
        var got = (await f.Store.GetAsync(exact.Id, default)).Value;
        got.RecallCount.Should().Be(1);
        got.LastRecalledAt.Should().Be(f.Clock.GetUtcNow());
    }

    [Fact]
    public async Task Ties_break_by_importance_then_recency()
    {
        var f = new MemoryServiceFixture();
        var svc = f.Build();
        var low = (await svc.RememberAsync(MemoryServiceFixture.Remember("kilo lima", importance: 0.2), default)).Value;
        f.Options.Dedupe.Enabled = false; // identical texts on purpose
        var high = (await svc.RememberAsync(MemoryServiceFixture.Remember("kilo lima", importance: 0.8), default)).Value;
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        var newer = (await svc.RememberAsync(MemoryServiceFixture.Remember("kilo lima", importance: 0.8), default)).Value;

        var r = (await svc.RecallAsync("kilo lima", new MemoryScope("alice", null), Opts(), default)).Value;
        r.Select(m => m.Record.Id).Should().Equal(newer.Id, high.Id, low.Id);
    }

    [Fact]
    public async Task Archived_and_stale_index_entries_are_dropped_and_scope_is_enforced()
    {
        var f = new MemoryServiceFixture();
        var svc = f.Build();
        var archived = (await svc.RememberAsync(MemoryServiceFixture.Remember("mike november"), default)).Value;
        await f.Store.UpdateAsync(archived.Id, new MemoryUpdate { IsArchived = true }, default);
        var deleted = (await svc.RememberAsync(MemoryServiceFixture.Remember("mike november oscar"), default)).Value;
        await f.Store.DeleteAsync(deleted.Id, default); // vector still in the index
        await svc.RememberAsync(MemoryServiceFixture.Remember("mike november papa", owner: "bob"), default);

        (await svc.RecallAsync("mike november", new MemoryScope("alice", null), Opts(), default)).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task TopK_and_MaxChars_cap_the_result()
    {
        var f = new MemoryServiceFixture();
        f.Options.Dedupe.Enabled = false;
        var svc = f.Build();
        var big = (await svc.RememberAsync(MemoryServiceFixture.Remember("quebec romeo " + new string('x', 150), importance: 1.0), default)).Value;
        var small1 = (await svc.RememberAsync(MemoryServiceFixture.Remember("quebec romeo sierra", importance: 0.5), default)).Value;
        var small2 = (await svc.RememberAsync(MemoryServiceFixture.Remember("quebec romeo tango", importance: 0.5), default)).Value;

        (await svc.RecallAsync("quebec romeo", new MemoryScope("alice", null), Opts(topK: 2, maxChars: 2000), default)).Value.Should().HaveCount(2);
        var budgeted = (await svc.RecallAsync("quebec romeo", new MemoryScope("alice", null), Opts(topK: 5, maxChars: 60), default)).Value;
        budgeted.Select(m => m.Record.Id).Should().BeEquivalentTo([small1.Id, small2.Id], "the 163-char memory does not fit; smaller ones still do");
        budgeted.Should().NotContain(m => m.Record.Id == big.Id);
    }

    [Fact]
    public async Task Blank_query_is_empty_and_index_failure_is_returned()
    {
        var f = new MemoryServiceFixture();
        var svc = f.Build();
        (await svc.RecallAsync("  ", new MemoryScope("alice", null), Opts(), default)).Value.Should().BeEmpty();

        f.Index = UnavailableMemoryIndex.Instance;
        var r = await f.Build().RecallAsync("anything", new MemoryScope("alice", null), Opts(), default);
        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be(AgentErrorCode.MemoryIndexUnavailable);
    }

    [Fact]
    public async Task TopK_below_one_is_clamped_and_the_options_instance_is_not_mutated()
    {
        var f = new MemoryServiceFixture();
        var svc = f.Build();
        await svc.RememberAsync(MemoryServiceFixture.Remember("victor whiskey"), default);
        var opts = Opts(topK: 0);

        var r = await svc.RecallAsync("victor whiskey", new MemoryScope("alice", null), opts, default);

        r.Value.Should().ContainSingle();
        opts.TopK.Should().Be(0, "the bound options object is shared and must never be mutated");
    }
}
