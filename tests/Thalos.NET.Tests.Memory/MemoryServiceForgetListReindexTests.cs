using Thalos.Memory;
using Thalos.Testing;
using ZeroAlloc.Results;

namespace Thalos.Tests.Memory;

public sealed class MemoryServiceForgetListReindexTests
{
    [Fact]
    public async Task Forget_soft_archives_hard_deletes_and_removes_from_index()
    {
        var f = new MemoryServiceFixture();
        f.Options.Dedupe.Enabled = false;
        var svc = f.Build();
        var a = (await svc.RememberAsync(MemoryServiceFixture.Remember("uniform victor"), default)).Value;
        var b = (await svc.RememberAsync(MemoryServiceFixture.Remember("uniform victor whiskey"), default)).Value;

        (await svc.ForgetAsync(a.Id, new MemoryScope("alice", null), hard: false, default)).IsSuccess.Should().BeTrue();
        (await f.Store.GetAsync(a.Id, default)).Value.IsArchived.Should().BeTrue();
        (await svc.ForgetAsync(b.Id, new MemoryScope("alice", null), hard: true, default)).IsSuccess.Should().BeTrue();
        (await f.Store.GetAsync(b.Id, default)).Error.Code.Should().Be(AgentErrorCode.MemoryNotFound);
        (await f.Index.SearchAsync("uniform victor", new MemoryScope("alice", null), new MemorySearchOptions(5, 0), default)).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Soft_forget_marks_the_record_pending_so_an_unarchived_record_is_reindexed()
    {
        var f = new MemoryServiceFixture();
        f.Options.Dedupe.Enabled = false;
        var svc = f.Build();
        var a = (await svc.RememberAsync(MemoryServiceFixture.Remember("whiskey xray yankee"), default)).Value;
        a.IndexPending.Should().BeFalse();

        (await svc.ForgetAsync(a.Id, new MemoryScope("alice", null), hard: false, default)).IsSuccess.Should().BeTrue();
        var archived = (await f.Store.GetAsync(a.Id, default)).Value;
        archived.IsArchived.Should().BeTrue();
        archived.IndexPending.Should().BeTrue("the vector was removed, so the record must be re-embedded if it ever comes back");
        (await svc.ReindexAsync(new ReindexOptions(), default)).Value.Should().Be(new ReindexReport(0, 0, 0), "archived records are never reindexed");

        // a host un-archives it (e.g. an admin API): the next pending-only reindex picks it up and recall finds it again
        await f.Store.UpdateAsync(a.Id, new MemoryUpdate { IsArchived = false }, default);
        (await svc.ReindexAsync(new ReindexOptions(), default)).Value.Should().Be(new ReindexReport(1, 1, 0));
        (await f.Store.GetAsync(a.Id, default)).Value.IndexPending.Should().BeFalse();
        (await svc.RecallAsync("whiskey xray yankee", new MemoryScope("alice", null), new RecallOptions(), default)).Value.Should().ContainSingle(m => m.Record.Id == a.Id);
    }

    [Fact]
    public async Task Forget_enforces_owner_and_reports_not_found()
    {
        var f = new MemoryServiceFixture();
        var svc = f.Build();
        var a = (await svc.RememberAsync(MemoryServiceFixture.Remember("xray yankee"), default)).Value;
        (await svc.ForgetAsync(a.Id, new MemoryScope("bob", null), hard: false, default)).Error.Code.Should().Be(AgentErrorCode.MemoryForbidden);
        (await svc.ForgetAsync(a.Id, new MemoryScope("bob", null, "alice"), hard: false, default)).Error.Code.Should().Be(AgentErrorCode.MemoryForbidden, "the shared owner never grants forget");
        (await svc.ForgetAsync(MemoryId.New(), new MemoryScope("alice", null), hard: false, default)).Error.Code.Should().Be(AgentErrorCode.MemoryNotFound);
        (await f.Store.GetAsync(a.Id, default)).Value.IsArchived.Should().BeFalse("a forbidden forget must not touch the record");
    }

    [Fact]
    public async Task List_requires_an_owner_and_delegates_to_the_store()
    {
        var f = new MemoryServiceFixture();
        var svc = f.Build();
        await svc.RememberAsync(MemoryServiceFixture.Remember("zulu"), default);
        (await svc.ListAsync(new MemoryQuery(), default)).Error.Code.Should().Be(AgentErrorCode.MemoryValidationFailed);
        (await svc.ListAsync(new MemoryQuery { OwnerIds = [] }, default)).Error.Code.Should().Be(AgentErrorCode.MemoryValidationFailed);
        (await svc.ListAsync(new MemoryQuery { OwnerIds = ["alice"] }, default)).Value.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task Reindex_embeds_pending_records_and_clears_the_flag()
    {
        var f = new MemoryServiceFixture(UnavailableMemoryIndex.Instance);
        var svc = f.Build();
        var pending = (await svc.RememberAsync(MemoryServiceFixture.Remember("alpha beta gamma"), default)).Value;
        pending.IndexPending.Should().BeTrue();
        (await svc.ReindexAsync(new ReindexOptions(), default)).Error.Code.Should().Be(AgentErrorCode.MemoryIndexUnavailable, "probe fails fast");

        f.Index = new InMemoryMemoryIndex(new HashedBagOfWordsEmbeddingGenerator());
        var svc2 = f.Build();
        var report = await svc2.ReindexAsync(new ReindexOptions { BatchSize = 1 }, default);

        report.IsSuccess.Should().BeTrue();
        report.Value.Should().Be(new ReindexReport(Scanned: 1, Indexed: 1, Failed: 0));
        (await f.Store.GetAsync(pending.Id, default)).Value.IndexPending.Should().BeFalse();
        (await svc2.RecallAsync("alpha beta gamma", new MemoryScope("alice", null), new RecallOptions { MinScore = 0.5 }, default)).Value.Should().ContainSingle();
        (await svc2.ReindexAsync(new ReindexOptions(), default)).Value.Scanned.Should().Be(0, "nothing pending any more");
        (await svc2.ReindexAsync(new ReindexOptions { PendingOnly = false }, default)).Value.Scanned.Should().Be(1);
    }

    /// <summary>Delegates to a real index but fails the first <paramref name="failUpserts"/> upsert batches (probe stays available).</summary>
    private sealed class FlakyIndex(IMemoryIndex inner, int failUpserts) : IMemoryIndex
    {
        private int _remaining = failUpserts;

        public ValueTask<UnitResult<AgentError>> UpsertAsync(IReadOnlyList<MemoryRecord> records, CancellationToken ct) =>
            _remaining-- > 0
                ? new(UnitResult<AgentError>.Failure(AgentError.MemoryIndexFailed("flaky", "Test")))
                : inner.UpsertAsync(records, ct);

        public ValueTask<Result<IReadOnlyList<MemoryHit>, AgentError>> SearchAsync(string query, MemoryScope scope, MemorySearchOptions options, CancellationToken ct) => inner.SearchAsync(query, scope, options, ct);
        public ValueTask<UnitResult<AgentError>> RemoveAsync(MemoryId id, CancellationToken ct) => inner.RemoveAsync(id, ct);
        public ValueTask<Result<MemoryIndexHealth, AgentError>> ProbeAsync(CancellationToken ct) => inner.ProbeAsync(ct);
    }

    [Fact]
    public async Task Reindex_counts_a_failed_batch_as_failed_and_leaves_its_records_pending()
    {
        var f = new MemoryServiceFixture(UnavailableMemoryIndex.Instance);
        var svc = f.Build();
        var ids = new List<MemoryId>();
        foreach (var text in new[] { "one uno", "two dos", "three tres" })
        {
            ids.Add((await svc.RememberAsync(MemoryServiceFixture.Remember(text), default)).Value.Id);
            f.Clock.Advance(TimeSpan.FromSeconds(1)); // distinct CreatedAt: the stream order (oldest first) must not rest on id ordering
        }

        var real = new InMemoryMemoryIndex(new HashedBagOfWordsEmbeddingGenerator());
        f.Index = new FlakyIndex(real, failUpserts: 1);
        var report = (await f.Build().ReindexAsync(new ReindexOptions { BatchSize = 2 }, default)).Value;

        report.Should().Be(new ReindexReport(Scanned: 3, Indexed: 1, Failed: 2), "the first batch of two failed, the trailing batch of one succeeded");
        var stillPending = new List<MemoryId>();
        foreach (var id in ids)
        {
            if ((await f.Store.GetAsync(id, default)).Value.IndexPending)
            {
                stillPending.Add(id);
            }
        }

        stillPending.Should().HaveCount(2, "a failed batch writes nothing, so IndexPending stays authoritative");

        f.Index = real;
        (await f.Build().ReindexAsync(new ReindexOptions(), default)).Value.Should().Be(new ReindexReport(Scanned: 2, Indexed: 2, Failed: 0));
        (await f.Store.ListAsync(new MemoryQuery { IndexPending = true }, default)).Value.TotalCount.Should().Be(0);
    }
}
