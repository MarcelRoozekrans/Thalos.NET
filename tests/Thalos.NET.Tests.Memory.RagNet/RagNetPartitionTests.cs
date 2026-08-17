using System.Globalization;
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
    public async Task Unicode_and_max_length_texts_round_trip_through_pgvector()
    {
        var unicode = Rec("alice", null, "café naïve Zürich 日本語のメモ emoji 🚀 — dash");
        var text4000 = string.Join(' ', Enumerable.Range(0, 400).Select(i => string.Create(CultureInfo.InvariantCulture, $"tok{i:D4}"))).PadRight(MemoryRecord.MaxTextLength, 'x');
        text4000.Length.Should().Be(MemoryRecord.MaxTextLength);
        var longest = Rec("alice", null, text4000);
        (await _index.UpsertAsync([unicode, longest], default)).IsSuccess.Should().BeTrue();

        var rows = await _store.SearchAsync(_bow.Embed(unicode.Text), new SearchOptions { TopK = 1 }, default);
        rows.Should().ContainSingle().Which.Chunk.Text.Should().Be(unicode.Text);
        var longRows = await _store.SearchAsync(_bow.Embed(text4000), new SearchOptions { TopK = 1 }, default);
        longRows.Should().ContainSingle().Which.Chunk.Text.Should().Be(text4000);

        (await _index.SearchAsync("日本語のメモ 🚀", new MemoryScope("alice", null), new MemorySearchOptions(1, 0.1), default)).Value.Should().ContainSingle().Which.Id.Should().Be(unicode.Id);
        (await _index.SearchAsync(text4000, new MemoryScope("alice", null), new MemorySearchOptions(1, 0.9), default)).Value.Should().ContainSingle().Which.Id.Should().Be(longest.Id);
    }

    [Fact]
    public async Task Probe_against_a_live_table_of_another_dimension_reports_unavailable_with_a_sql_state()
    {
        // the fixture table is vector(64) here; a 128-dim store + generator over it must not throw, just report.
        // Postgres only evaluates <=> on existing rows, so an *empty* mismatched table probes as available — the schema
        // initializer's InitializeAsync is the guard for that case; here one 64-dim row makes the mismatch observable.
        (await _index.UpsertAsync([Rec("alice", null, "tango uniform victor")], default)).IsSuccess.Should().BeTrue();
        using var store128 = new PgVectorStore(pg.ConnectionString, 128);
        using var bow128 = new HashedBagOfWordsEmbeddingGenerator(128);
        var index = new RagNetMemoryIndex(store128, bow128, new RagNetMemoryOptions { ConnectionString = pg.ConnectionString, VectorDimensions = 128 });

        var health = await index.ProbeAsync(default);
        health.IsSuccess.Should().BeTrue();
        health.Value.Available.Should().BeFalse();
        health.Value.Detail.Should().HaveLength(5, "the detail is the SQL state, not the message").And.NotContain("vector");
        health.Value.Dimensions.Should().BeNull();

        var upsert = await index.UpsertAsync([Rec("alice", null, "quebec romeo sierra")], default);
        upsert.IsFailure.Should().BeTrue();
        upsert.Error.Code.Should().Be(AgentErrorCode.MemoryIndexFailed);
        upsert.Error.Detail.Should().HaveLength(5);
    }
}
