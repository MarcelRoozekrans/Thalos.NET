using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.PgVector;
using Thalos.Memory;
using Thalos.Memory.RagNet;
using Thalos.Testing;

namespace Thalos.Tests.Memory.RagNet;

[Collection(PgVectorCollection.Name)]
[Trait("Category", "Docker")]
#pragma warning disable CA1001 // _store/_bow are disposed in IAsyncLifetime.DisposeAsync
public sealed class RagNetPartitionTests(PgVectorFixture pg) : IAsyncLifetime
#pragma warning restore CA1001
{
    private readonly HashedBagOfWordsEmbeddingGenerator _bow = new(64);
    private PgVectorStore _store = null!;
    private RagNetMemoryIndex _index = null!;

    public async Task InitializeAsync()
    {
        _store = new PgVectorStore(pg.ConnectionString, 64);
        await pg.ResetAsync();
        await _store.InitializeAsync();
        _index = new RagNetMemoryIndex(_store, _bow, new RagNetMemoryOptions { ConnectionString = pg.ConnectionString, VectorDimensions = 64 });
    }

    public Task DisposeAsync() { _store.Dispose(); _bow.Dispose(); return Task.CompletedTask; }

    private static MemoryRecord Rec(string owner, AgentId? agent, string text) => new()
    {
        Id = MemoryId.New(), OwnerId = owner, AgentId = agent, Kind = MemoryKind.Fact, Text = text, CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task Rows_carry_the_thalos_marker_owner_agent_and_kind_metadata()
    {
        var agent = AgentId.New();
        var r = Rec("alice", agent, "hotel india juliet");
        (await _index.UpsertAsync([r], default)).IsSuccess.Should().BeTrue();

        var rows = await _store.SearchAsync(_bow.Embed("hotel india juliet"), new SearchOptions { TopK = 5 }, default);
        var chunk = rows.Should().ContainSingle().Which.Chunk;
        chunk.DocumentId.Value.Should().Be(r.Id.ToString());
        chunk.Metadata["thalos"].StringValue.Should().Be("memory");
        chunk.Metadata["owner_id"].StringValue.Should().Be("alice");
        chunk.Metadata["agent_id"].StringValue.Should().Be(agent.ToString());
        chunk.Metadata["kind"].StringValue.Should().Be("fact");
    }

    [Fact]
    public async Task Foreign_rag_chunks_rows_and_other_owners_never_leak_into_a_search()
    {
        await _store.StoreAsync([new EmbeddedChunk
        {
            Chunk = new TextChunk { Text = "kilo lima mike (foreign document)", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 },
            Embedding = _bow.Embed("kilo lima mike (foreign document)"),
        }], default);
        await _index.UpsertAsync([Rec("bob", null, "kilo lima mike (bob)"), Rec("alice", null, "kilo lima mike (alice)")], default);

        var hits = (await _index.SearchAsync("kilo lima mike", new MemoryScope("alice", null), new MemorySearchOptions(10, 0), default)).Value;
        hits.Should().ContainSingle();
    }

    [Fact]
    public async Task Same_id_across_partitions_keeps_the_best_score_once()
    {
        // one memory is visible via two partitions when the shared owner equals the caller (owner-wide + pinned are different rows only if ids differ);
        // upserting the same id twice with different agents replaces the row, so at most one hit per id
        var r = Rec("alice", null, "november oscar papa");
        await _index.UpsertAsync([r], default);
        var hits = (await _index.SearchAsync("november oscar papa", new MemoryScope("alice", AgentId.New(), "alice"), new MemorySearchOptions(10, 0), default)).Value;
        hits.Should().ContainSingle().Which.Id.Should().Be(r.Id);
    }
}
