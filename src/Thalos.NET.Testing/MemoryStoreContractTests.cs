using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Thalos.Memory;
using Xunit;

namespace Thalos.Testing;

/// <summary>
/// Behavioural contract every <see cref="IMemoryStore"/> must satisfy — the suite Thalos runs against <c>InMemoryMemoryStore</c>.
/// Derive, implement <see cref="CreateStoreAsync"/> (fresh, empty store reading time from the given clock), let xUnit discover
/// the inherited facts. Timestamps are compared with 1 ms tolerance; stores must keep millisecond precision.
/// </summary>
/// <remarks>
/// What the suite assumes beyond the interface docs: <c>UpdatedAt</c> is stamped from the injected <see cref="TimeProvider"/> (not a
/// database-side <c>now()</c>); <see cref="MemoryRecord.Importance"/> round-trips as an exact <see cref="double"/> (a <c>real</c>/float4
/// column fails); a query without <see cref="MemoryQuery.OwnerIds"/> lists all owners (store level — the service adds the owner
/// requirement); empty-but-non-null filter lists mean "no filter"; custom <see cref="MemoryKind"/> values round-trip and filter;
/// <c>GetAsync</c> returns archived records and <c>MarkRecalledAsync</c> counts on them; texts/sources/tags at the exact limits (4000-char
/// multi-line non-BMP text, 256-char source, ten 32-char tags) round-trip; <see cref="MemoryQuery.Page"/> up to <see cref="int.MaxValue"/>
/// must not overflow the skip arithmetic; <see cref="IMemoryStore.StreamAsync"/> must keep yielding every match exactly once while the
/// consumer updates already-yielded records (snapshot or keyset paging — see its docs); and the store must be safe for concurrent calls
/// (twenty parallel <c>MarkRecalledAsync</c> calls on one record must lose nothing).
/// </remarks>
public abstract class MemoryStoreContractTests
{
    /// <summary>Creates a fresh, empty store whose clock is <paramref name="clock"/> (a <see cref="FakeTimeProvider"/> the suite advances; stamp <c>UpdatedAt</c> from it).</summary>
    protected abstract ValueTask<IMemoryStore> CreateStoreAsync(TimeProvider clock);

    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(1);

    /// <summary>A fake clock starting at 2026-08-17 12:00 UTC (advance it between operations).</summary>
    protected static FakeTimeProvider NewClock() => new(new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero));

    /// <summary>A valid record timestamped from <paramref name="clock"/> (defaults: owner <c>alice</c>, kind <c>fact</c>, one tag).</summary>
    protected static MemoryRecord NewRecord(TimeProvider clock, string owner = "alice", AgentId? agent = null, string text = "The user prefers xUnit.", MemoryKind? kind = null, IReadOnlyList<string>? tags = null, bool indexPending = false)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var now = clock.GetUtcNow();
        return new MemoryRecord { Id = MemoryId.New(), OwnerId = owner, AgentId = agent, Kind = kind ?? MemoryKind.Fact, Text = text, Tags = tags ?? ["testing"], Source = "test", Importance = 0.5, CreatedAt = now, UpdatedAt = now, IndexPending = indexPending };
    }

    [Fact]
    public async Task Create_then_Get_roundtrips_every_field()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var agent = AgentId.New();
        var record = NewRecord(clock, agent: agent, tags: ["a", "b"]) with { Importance = 0.8, Source = "api" };

        var created = await store.CreateAsync(record, CancellationToken.None);
        created.IsSuccess.Should().BeTrue(created.IsFailure ? created.Error.ToString() : "");

        var got = await store.GetAsync(record.Id, CancellationToken.None);
        got.IsSuccess.Should().BeTrue();
        got.Value.Should().BeEquivalentTo(record, o => o.Excluding(r => r.CreatedAt).Excluding(r => r.UpdatedAt));
        got.Value.CreatedAt.Should().BeCloseTo(record.CreatedAt, Tolerance);
        got.Value.UpdatedAt.Should().BeCloseTo(record.UpdatedAt, Tolerance);
        got.Value.Tags.Should().Equal("a", "b");
        got.Value.AgentId.Should().Be(agent);
    }

    [Fact]
    public async Task Create_and_Update_normalise_tags()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var record = NewRecord(clock, tags: ["Foo", " foo "]);

        var created = await store.CreateAsync(record, CancellationToken.None);
        created.IsSuccess.Should().BeTrue();
        created.Value.Tags.Should().Equal("foo");
        (await store.GetAsync(record.Id, CancellationToken.None)).Value.Tags.Should().Equal("foo");

        var updated = await store.UpdateAsync(record.Id, new MemoryUpdate { Tags = ["Bar", " bar ", "baz"] }, CancellationToken.None);
        updated.IsSuccess.Should().BeTrue();
        updated.Value.Tags.Should().Equal("bar", "baz");
        (await store.GetAsync(record.Id, CancellationToken.None)).Value.Tags.Should().Equal("bar", "baz");
    }

    [Fact]
    public async Task Create_duplicate_id_fails_with_MemoryStoreFailed()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var record = NewRecord(clock);
        (await store.CreateAsync(record, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var again = await store.CreateAsync(record, CancellationToken.None);
        again.IsFailure.Should().BeTrue();
        again.Error.Code.Should().Be(AgentErrorCode.MemoryStoreFailed);
    }

    [Fact]
    public async Task Get_Update_Delete_unknown_return_MemoryNotFound()
    {
        var store = await CreateStoreAsync(NewClock());
        var id = MemoryId.New();
        (await store.GetAsync(id, CancellationToken.None)).Error.Code.Should().Be(AgentErrorCode.MemoryNotFound);
        (await store.UpdateAsync(id, new MemoryUpdate { Text = "x" }, CancellationToken.None)).Error.Code.Should().Be(AgentErrorCode.MemoryNotFound);
        (await store.DeleteAsync(id, CancellationToken.None)).Error.Code.Should().Be(AgentErrorCode.MemoryNotFound);
    }

    [Fact]
    public async Task Update_applies_only_set_members_and_bumps_UpdatedAt_for_content_changes()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var record = NewRecord(clock);
        await store.CreateAsync(record, CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(5));
        var updated = await store.UpdateAsync(record.Id, new MemoryUpdate { Text = "The user prefers xUnit over NUnit.", Importance = 0.9 }, CancellationToken.None);
        updated.IsSuccess.Should().BeTrue();
        updated.Value.Text.Should().Be("The user prefers xUnit over NUnit.");
        updated.Value.Importance.Should().Be(0.9);
        updated.Value.Tags.Should().Equal(["testing"], "unset members are unchanged");
        updated.Value.IsArchived.Should().BeFalse();
        updated.Value.UpdatedAt.Should().BeCloseTo(clock.GetUtcNow(), Tolerance);
        updated.Value.CreatedAt.Should().BeCloseTo(record.CreatedAt, Tolerance);

        var got = (await store.GetAsync(record.Id, CancellationToken.None)).Value;
        got.Should().BeEquivalentTo(updated.Value, o => o.Excluding(r => r.CreatedAt).Excluding(r => r.UpdatedAt));
        got.UpdatedAt.Should().BeCloseTo(updated.Value.UpdatedAt, Tolerance);
    }

    [Fact]
    public async Task Update_of_IndexPending_alone_does_not_bump_UpdatedAt()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var record = NewRecord(clock, indexPending: true);
        await store.CreateAsync(record, CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(10));
        var updated = await store.UpdateAsync(record.Id, new MemoryUpdate { IndexPending = false }, CancellationToken.None);
        updated.IsSuccess.Should().BeTrue();
        updated.Value.IndexPending.Should().BeFalse();
        updated.Value.UpdatedAt.Should().BeCloseTo(record.UpdatedAt, Tolerance);
    }

    [Fact]
    public async Task Archive_via_update_and_hard_delete()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var record = NewRecord(clock);
        await store.CreateAsync(record, CancellationToken.None);

        (await store.UpdateAsync(record.Id, new MemoryUpdate { IsArchived = true }, CancellationToken.None)).Value.IsArchived.Should().BeTrue();
        (await store.ListAsync(new MemoryQuery { OwnerIds = ["alice"] }, CancellationToken.None)).Value.Items.Should().BeEmpty("archived is excluded by default");
        (await store.ListAsync(new MemoryQuery { OwnerIds = ["alice"], IncludeArchived = true }, CancellationToken.None)).Value.Items.Should().ContainSingle();

        (await store.DeleteAsync(record.Id, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await store.GetAsync(record.Id, CancellationToken.None)).Error.Code.Should().Be(AgentErrorCode.MemoryNotFound);
    }

    [Fact]
    public async Task List_filters_by_owners_agent_kinds_tags_and_pending()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var agent = AgentId.New();
        var a1 = NewRecord(clock, "alice", null, "a1", MemoryKind.Fact, ["x"]);
        var a2 = NewRecord(clock, "alice", agent, "a2", MemoryKind.Learning, ["x", "y"], indexPending: true);
        var b1 = NewRecord(clock, "bob", null, "b1", MemoryKind.Fact, ["x"]);
        var s1 = NewRecord(clock, "shared", null, "s1", MemoryKind.Learning, ["y"]);
        foreach (var r in new[] { a1, a2, b1, s1 })
        {
            (await store.CreateAsync(r, CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        static IEnumerable<string> Texts(MemoryPage page) => page.Items.Select(i => i.Text);
        Texts((await store.ListAsync(new MemoryQuery { OwnerIds = ["alice"] }, CancellationToken.None)).Value).Should().BeEquivalentTo(["a1", "a2"]);
        Texts((await store.ListAsync(new MemoryQuery { OwnerIds = ["alice", "shared"] }, CancellationToken.None)).Value).Should().BeEquivalentTo(["a1", "a2", "s1"]);
        Texts((await store.ListAsync(new MemoryQuery { OwnerIds = ["alice"], AgentId = agent }, CancellationToken.None)).Value).Should().BeEquivalentTo(["a2"]);
        Texts((await store.ListAsync(new MemoryQuery { Kinds = [MemoryKind.Learning] }, CancellationToken.None)).Value).Should().BeEquivalentTo(["a2", "s1"]);
        Texts((await store.ListAsync(new MemoryQuery { Tags = ["x", "y"] }, CancellationToken.None)).Value).Should().BeEquivalentTo(["a2"]);
        Texts((await store.ListAsync(new MemoryQuery { Tags = ["X ", "Y"] }, CancellationToken.None)).Value).Should().BeEquivalentTo(["a2"], "query tags are normalised like stored tags");
        Texts((await store.ListAsync(new MemoryQuery { IndexPending = true }, CancellationToken.None)).Value).Should().BeEquivalentTo(["a2"]);
        Texts((await store.ListAsync(new MemoryQuery { IndexPending = false }, CancellationToken.None)).Value).Should().BeEquivalentTo(["a1", "b1", "s1"]);
        (await store.ListAsync(new MemoryQuery { OwnerIds = ["nobody"] }, CancellationToken.None)).Value.Items.Should().BeEmpty();

        var streamed = new List<string>();
        await foreach (var r in store.StreamAsync(new MemoryQuery { Tags = ["X ", "Y"] }, CancellationToken.None))
        {
            streamed.Add(r.Text);
        }

        streamed.Should().Equal(["a2"], "stream applies the same normalised tag filter");
    }

    [Fact]
    public async Task Blank_tags_are_dropped_on_create_and_empty_update_tags_clear()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var record = NewRecord(clock, tags: ["", "  ", "keep"]);
        (await store.CreateAsync(record, CancellationToken.None)).Value.Tags.Should().Equal("keep");

        var cleared = await store.UpdateAsync(record.Id, new MemoryUpdate { Tags = [] }, CancellationToken.None);
        cleared.IsSuccess.Should().BeTrue();
        cleared.Value.Tags.Should().BeEmpty();
        (await store.GetAsync(record.Id, CancellationToken.None)).Value.Tags.Should().BeEmpty();
    }

    [Fact]
    public async Task List_orders_by_UpdatedAt_desc_and_pages_with_total()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var ids = new List<MemoryId>();
        for (var i = 0; i < 5; i++)
        {
            var r = NewRecord(clock, text: $"m{i}");
            ids.Add(r.Id);
            (await store.CreateAsync(r, CancellationToken.None)).IsSuccess.Should().BeTrue();
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        var page1 = (await store.ListAsync(new MemoryQuery { OwnerIds = ["alice"], Page = 1, PageSize = 2 }, CancellationToken.None)).Value;
        page1.TotalCount.Should().Be(5);
        page1.Page.Should().Be(1);
        page1.PageSize.Should().Be(2);
        page1.Items.Select(i => i.Text).Should().Equal("m4", "m3");
        var page3 = (await store.ListAsync(new MemoryQuery { OwnerIds = ["alice"], Page = 3, PageSize = 2 }, CancellationToken.None)).Value;
        page3.Items.Select(i => i.Text).Should().Equal("m0");
        (await store.ListAsync(new MemoryQuery { OwnerIds = ["alice"], Page = 9, PageSize = 2 }, CancellationToken.None)).Value.Items.Should().BeEmpty();

        // an update moves a record to the front
        await store.UpdateAsync(ids[0], new MemoryUpdate { Importance = 1 }, CancellationToken.None);
        (await store.ListAsync(new MemoryQuery { OwnerIds = ["alice"], PageSize = 1 }, CancellationToken.None)).Value.Items.Single().Text.Should().Be("m0");
    }

    [Fact]
    public async Task List_clamps_page_size_to_100_and_page_to_at_least_1()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        await store.CreateAsync(NewRecord(clock), CancellationToken.None);
        var page = (await store.ListAsync(new MemoryQuery { PageSize = 1000 }, CancellationToken.None)).Value;
        page.PageSize.Should().Be(MemoryQuery.MaxPageSize);

        var first = (await store.ListAsync(new MemoryQuery { Page = 0, PageSize = 10 }, CancellationToken.None)).Value;
        first.Page.Should().Be(1);
        first.Items.Should().ContainSingle();
        (await store.ListAsync(new MemoryQuery { Page = -5, PageSize = 10 }, CancellationToken.None)).Value.Page.Should().Be(1);
        (await store.ListAsync(new MemoryQuery { Page = int.MaxValue, PageSize = 100 }, CancellationToken.None)).Value.Items.Should().BeEmpty("a huge page must not overflow");
    }

    [Fact]
    public async Task List_pages_records_with_identical_UpdatedAt_without_gaps_or_duplicates()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var ids = new List<MemoryId>();
        for (var i = 0; i < 7; i++)
        {
            var r = NewRecord(clock, text: $"same{i}");
            ids.Add(r.Id);
            (await store.CreateAsync(r, CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        var page1 = (await store.ListAsync(new MemoryQuery { OwnerIds = ["alice"], Page = 1, PageSize = 4 }, CancellationToken.None)).Value;
        var page2 = (await store.ListAsync(new MemoryQuery { OwnerIds = ["alice"], Page = 2, PageSize = 4 }, CancellationToken.None)).Value;
        page1.Items.Should().HaveCount(4);
        page2.Items.Should().HaveCount(3);
        page1.Items.Concat(page2.Items).Select(r => r.Id).Should().BeEquivalentTo(ids, "pages partition the matches even when UpdatedAt ties");
        page1.Items.Select(r => r.Id).Should().OnlyHaveUniqueItems();
        page1.Items.Select(r => r.Id).Should().NotIntersectWith(page2.Items.Select(r => r.Id));
    }

    [Fact]
    public async Task MarkRecalled_increments_count_sets_timestamp_and_ignores_unknown_ids()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var r = NewRecord(clock);
        await store.CreateAsync(r, CancellationToken.None);
        var at = clock.GetUtcNow().AddMinutes(1);

        (await store.MarkRecalledAsync([r.Id, MemoryId.New()], at, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await store.MarkRecalledAsync([r.Id, r.Id], at.AddMinutes(1), CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await store.MarkRecalledAsync([], at, CancellationToken.None)).IsSuccess.Should().BeTrue();

        var got = (await store.GetAsync(r.Id, CancellationToken.None)).Value;
        got.RecallCount.Should().Be(2, "ids are a set: a duplicate id counts once");
        got.LastRecalledAt.Should().NotBeNull();
        got.LastRecalledAt!.Value.Should().BeCloseTo(at.AddMinutes(1), Tolerance);
        got.UpdatedAt.Should().BeCloseTo(r.UpdatedAt, Tolerance, "recall is not a content change");
    }

    [Fact]
    public async Task Stream_yields_every_match_oldest_first_ignoring_paging()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        for (var i = 0; i < 7; i++)
        {
            await store.CreateAsync(NewRecord(clock, text: $"m{i}", indexPending: i % 2 == 0), CancellationToken.None);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        var texts = new List<string>();
        await foreach (var r in store.StreamAsync(new MemoryQuery { IndexPending = true, PageSize = 1 }, CancellationToken.None))
        {
            texts.Add(r.Text);
        }

        texts.Should().Equal("m0", "m2", "m4", "m6");
    }

    [Fact]
    public async Task Stream_tolerates_updates_to_yielded_records_and_yields_each_match_exactly_once()
    {
        // reindex clears IndexPending on records it just received while the stream is still open: OFFSET paging over the filtered
        // set would skip rows as matches drop out of the filter; a snapshot or keyset paging by (CreatedAt, Id) must not
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var expected = new List<MemoryId>();
        for (var i = 0; i < 12; i++)
        {
            expected.Add((await store.CreateAsync(NewRecord(clock, text: $"pending {i}", indexPending: true), CancellationToken.None)).Value.Id);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        var yielded = new List<MemoryId>();
        await foreach (var r in store.StreamAsync(new MemoryQuery { IndexPending = true, PageSize = 3 }, CancellationToken.None))
        {
            yielded.Add(r.Id);
            (await store.UpdateAsync(r.Id, new MemoryUpdate { IndexPending = false }, CancellationToken.None)).IsSuccess.Should().BeTrue("updating a yielded record while the stream is open must work");
        }

        yielded.Should().Equal(expected, "every match once, oldest first, whatever paging the store uses internally");
        (await store.ListAsync(new MemoryQuery { IndexPending = true }, CancellationToken.None)).Value.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Custom_kind_roundtrips_and_filters()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var custom = new MemoryKind("ralph-learning");
        var record = NewRecord(clock, kind: custom);
        await store.CreateAsync(record, CancellationToken.None);
        await store.CreateAsync(NewRecord(clock, kind: MemoryKind.Fact), CancellationToken.None);

        (await store.GetAsync(record.Id, CancellationToken.None)).Value.Kind.Should().Be(custom);
        (await store.ListAsync(new MemoryQuery { OwnerIds = ["alice"], Kinds = [custom] }, CancellationToken.None)).Value.Items.Should().ContainSingle().Which.Id.Should().Be(record.Id);
        (await store.ListAsync(new MemoryQuery { OwnerIds = ["alice"], Kinds = [MemoryKind.Learning] }, CancellationToken.None)).Value.Items.Should().BeEmpty("a custom kind is not the built-in learning kind");
    }

    [Fact]
    public async Task Empty_filter_lists_mean_no_filter()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        await store.CreateAsync(NewRecord(clock, owner: "alice", kind: MemoryKind.Fact, tags: ["x"]), CancellationToken.None);
        await store.CreateAsync(NewRecord(clock, owner: "bob", kind: MemoryKind.Note, tags: []), CancellationToken.None);

        var query = new MemoryQuery { OwnerIds = [], Kinds = [], Tags = [] };
        (await store.ListAsync(query, CancellationToken.None)).Value.TotalCount.Should().Be(2, "empty lists are 'no filter', like null");
        var streamed = 0;
        await foreach (var _ in store.StreamAsync(query, CancellationToken.None))
        {
            streamed++;
        }

        streamed.Should().Be(2);
    }

    [Fact]
    public async Task Get_returns_archived_records()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var record = NewRecord(clock);
        await store.CreateAsync(record, CancellationToken.None);
        await store.UpdateAsync(record.Id, new MemoryUpdate { IsArchived = true }, CancellationToken.None);

        var got = await store.GetAsync(record.Id, CancellationToken.None);
        got.IsSuccess.Should().BeTrue("Get does not filter archived records — callers decide");
        got.Value.IsArchived.Should().BeTrue();
    }

    [Fact]
    public async Task Boundary_lengths_roundtrip()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var text = string.Concat(Enumerable.Repeat("memo 🚀\n", MemoryRecord.MaxTextLength / 8)); // 8 UTF-16 chars per unit: multi-line, non-BMP
        text.Length.Should().Be(MemoryRecord.MaxTextLength);
        var source = new string('s', MemoryRecord.MaxSourceLength);
        var tags = Enumerable.Range(0, MemoryRecord.MaxTags).Select(i => string.Concat(new string('t', MemoryRecord.MaxTagLength - 1), ((char)('a' + i)).ToString())).ToList();
        var record = NewRecord(clock, text: text, tags: tags) with { Source = source };
        MemoryRules.Validate(record).Should().BeNull("the record is at the limits, not over them");

        var created = await store.CreateAsync(record, CancellationToken.None);
        created.IsSuccess.Should().BeTrue(created.IsFailure ? created.Error.ToString() : "");
        var got = (await store.GetAsync(record.Id, CancellationToken.None)).Value;
        got.Text.Should().Be(text);
        got.Source.Should().Be(source);
        got.Tags.Should().Equal(tags);
    }

    [Fact]
    public async Task MarkRecalled_counts_on_archived_records_too()
    {
        // the service never recalls archived records; a store must not second-guess that (a stale index hit that was archived
        // between search and hydration is dropped by the service, not by the store)
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var record = NewRecord(clock);
        await store.CreateAsync(record, CancellationToken.None);
        await store.UpdateAsync(record.Id, new MemoryUpdate { IsArchived = true }, CancellationToken.None);

        (await store.MarkRecalledAsync([record.Id], clock.GetUtcNow(), CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await store.GetAsync(record.Id, CancellationToken.None)).Value.RecallCount.Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_MarkRecalled_calls_lose_nothing()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var r = NewRecord(clock);
        await store.CreateAsync(r, CancellationToken.None);

        const int n = 20;
        await Task.WhenAll(Enumerable.Range(0, n).Select(_ => Task.Run(async () =>
            (await store.MarkRecalledAsync([r.Id], clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false)).IsSuccess.Should().BeTrue())));

        (await store.GetAsync(r.Id, CancellationToken.None)).Value.RecallCount.Should().Be(n);
    }
}
