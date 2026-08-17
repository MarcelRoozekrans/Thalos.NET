using FluentAssertions;
using Microsoft.Extensions.AI;
using Thalos.Memory;
using Xunit;

namespace Thalos.Testing;

/// <summary>
/// Behavioural contract every <see cref="IMemoryIndex"/> must satisfy, run with the deterministic
/// <see cref="HashedBagOfWordsEmbeddingGenerator"/> (cosine = word overlap). Derive, implement <see cref="CreateIndexAsync(IEmbeddingGenerator{string, Embedding{float}})"/>
/// (fresh, empty index over the given generator; override <see cref="Dimensions"/> if your backend needs another size).
/// </summary>
public abstract class MemoryIndexContractTests
{
    protected virtual int Dimensions => 128;

    protected abstract ValueTask<IMemoryIndex> CreateIndexAsync(IEmbeddingGenerator<string, Embedding<float>> embeddings);

    protected ValueTask<IMemoryIndex> CreateIndexAsync() => CreateIndexAsync(new HashedBagOfWordsEmbeddingGenerator(Dimensions));

    private static readonly DateTimeOffset T0 = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    protected static MemoryRecord Rec(string owner, AgentId? agent, string text, MemoryKind? kind = null) => new()
    {
        Id = MemoryId.New(), OwnerId = owner, AgentId = agent, Kind = kind ?? MemoryKind.Fact, Text = text, CreatedAt = T0, UpdatedAt = T0,
    };

    private static MemorySearchOptions Any(int topK = 10) => new(topK, 0.0);

    [Fact]
    public async Task Upsert_then_search_ranks_by_similarity_with_unit_range_scores()
    {
        var index = await CreateIndexAsync();
        var xunit = Rec("alice", null, "The user prefers xUnit over NUnit for tests.");
        var playwright = Rec("alice", null, "Playwright locators on the PRD page use data-testid.");
        (await index.UpsertAsync([xunit, playwright], CancellationToken.None)).IsSuccess.Should().BeTrue();

        var hits = await index.SearchAsync("xUnit or NUnit for the tests?", new MemoryScope("alice", null), Any(), CancellationToken.None);
        hits.IsSuccess.Should().BeTrue(hits.IsFailure ? hits.Error.ToString() : "");
        hits.Value.Should().NotBeEmpty();
        hits.Value[0].Id.Should().Be(xunit.Id);
        hits.Value.Should().OnlyContain(h => h.Score >= 0 && h.Score <= 1.0001);
        hits.Value.Should().BeInDescendingOrder(h => h.Score);
    }

    [Fact]
    public async Task Search_never_crosses_owners()
    {
        var index = await CreateIndexAsync();
        await index.UpsertAsync([Rec("alice", null, "alice secret token"), Rec("bob", null, "bob secret token")], CancellationToken.None);
        var hits = (await index.SearchAsync("secret token", new MemoryScope("alice", null), Any(), CancellationToken.None)).Value;
        hits.Should().ContainSingle();
    }

    [Fact]
    public async Task Agent_pinned_memories_are_visible_only_to_that_agent()
    {
        var index = await CreateIndexAsync();
        var a = AgentId.New();
        var b = AgentId.New();
        var shared = Rec("alice", null, "shared note about deployment");
        var pinnedA = Rec("alice", a, "agent a note about deployment");
        var pinnedB = Rec("alice", b, "agent b note about deployment");
        await index.UpsertAsync([shared, pinnedA, pinnedB], CancellationToken.None);

        (await index.SearchAsync("note about deployment", new MemoryScope("alice", a), Any(), CancellationToken.None)).Value.Select(h => h.Id).Should().BeEquivalentTo([shared.Id, pinnedA.Id]);
        (await index.SearchAsync("note about deployment", new MemoryScope("alice", b), Any(), CancellationToken.None)).Value.Select(h => h.Id).Should().BeEquivalentTo([shared.Id, pinnedB.Id]);
        (await index.SearchAsync("note about deployment", new MemoryScope("alice", null), Any(), CancellationToken.None)).Value.Select(h => h.Id).Should().BeEquivalentTo([shared.Id]);
    }

    [Fact]
    public async Task Shared_owner_partition_is_included_only_when_configured()
    {
        var index = await CreateIndexAsync();
        var project = Rec("daedalus", null, "project learning about playwright locators");
        var pinnedShared = Rec("daedalus", AgentId.New(), "pinned shared-owner learning about playwright locators");
        await index.UpsertAsync([project, pinnedShared, Rec("alice", null, "unrelated")], CancellationToken.None);

        // MinScore > 0: alice's zero-overlap record scores exactly 0, which a "score >= MinScore" index (in-memory, pgvector) would return at 0.0
        var related = new MemorySearchOptions(10, 0.1);
        (await index.SearchAsync("playwright locators learning", new MemoryScope("alice", null, "daedalus"), related, CancellationToken.None)).Value.Select(h => h.Id).Should().BeEquivalentTo([project.Id]);
        (await index.SearchAsync("playwright locators learning", new MemoryScope("alice", null), related, CancellationToken.None)).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task MinScore_and_TopK_apply()
    {
        var index = await CreateIndexAsync();
        var exact = Rec("alice", null, "rotate the api key monthly");
        await index.UpsertAsync([exact, Rec("alice", null, "rotate the logs weekly"), Rec("alice", null, "monthly report")], CancellationToken.None);

        var strict = (await index.SearchAsync("rotate the api key monthly", new MemoryScope("alice", null), new MemorySearchOptions(10, 0.99), CancellationToken.None)).Value;
        strict.Should().ContainSingle().Which.Id.Should().Be(exact.Id);
        (await index.SearchAsync("rotate the api key monthly", new MemoryScope("alice", null), new MemorySearchOptions(1, 0.0), CancellationToken.None)).Value.Should().HaveCount(1);
        (await index.SearchAsync("rotate the api key monthly", new MemoryScope("alice", null), new MemorySearchOptions(10, 0.0), CancellationToken.None)).Value.Should().HaveCount(3);
    }

    [Fact]
    public async Task Upsert_same_id_replaces_the_vector()
    {
        var index = await CreateIndexAsync();
        var r = Rec("alice", null, "alpha bravo charlie");
        await index.UpsertAsync([r], CancellationToken.None);
        await index.UpsertAsync([r with { Text = "delta echo foxtrot" }], CancellationToken.None);

        (await index.SearchAsync("alpha bravo charlie", new MemoryScope("alice", null), new MemorySearchOptions(10, 0.5), CancellationToken.None)).Value.Should().BeEmpty();
        (await index.SearchAsync("delta echo foxtrot", new MemoryScope("alice", null), Any(), CancellationToken.None)).Value.Should().ContainSingle().Which.Id.Should().Be(r.Id);
    }

    [Fact]
    public async Task Remove_makes_it_unfindable_and_unknown_remove_succeeds()
    {
        var index = await CreateIndexAsync();
        var r = Rec("alice", null, "golf hotel india");
        await index.UpsertAsync([r], CancellationToken.None);
        (await index.RemoveAsync(r.Id, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await index.RemoveAsync(MemoryId.New(), CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await index.SearchAsync("golf hotel india", new MemoryScope("alice", null), Any(), CancellationToken.None)).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Empty_index_blank_query_and_empty_batch_are_successful_no_ops()
    {
        var index = await CreateIndexAsync();
        (await index.UpsertAsync([], CancellationToken.None)).IsSuccess.Should().BeTrue();
        var fresh = await index.SearchAsync("anything", new MemoryScope("alice", null), Any(), CancellationToken.None);
        fresh.IsSuccess.Should().BeTrue();
        fresh.Value.Should().BeEmpty();
        await index.UpsertAsync([Rec("alice", null, "something")], CancellationToken.None);
        (await index.SearchAsync("   ", new MemoryScope("alice", null), Any(), CancellationToken.None)).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Probe_reports_available()
    {
        var index = await CreateIndexAsync();
        var health = await index.ProbeAsync(CancellationToken.None);
        health.IsSuccess.Should().BeTrue();
        health.Value.Available.Should().BeTrue(health.Value.Detail);
        health.Value.Dimensions.Should().Match(d => d == null || d == Dimensions);
    }
}
