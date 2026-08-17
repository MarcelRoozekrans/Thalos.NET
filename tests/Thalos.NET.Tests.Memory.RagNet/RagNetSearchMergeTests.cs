using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Thalos.Memory;
using Thalos.Memory.RagNet;
using Thalos.Testing;

namespace Thalos.Tests.Memory.RagNet;

/// <summary>Partition-merge and batch semantics with a scripted <see cref="IVectorStore"/> (no Docker): one query per partition, every filter owner-scoped, the same id merged to its best score, duplicate ids in a batch stored once.</summary>
public sealed class RagNetSearchMergeTests
{
    private static RagNetMemoryIndex Index(IVectorStore store) =>
        new(store, new HashedBagOfWordsEmbeddingGenerator(16), new RagNetMemoryOptions { ConnectionString = "Host=x", VectorDimensions = 16 });

    private static SearchResult Hit(string documentId, double score) => new()
    {
        Chunk = new TextChunk { Text = "t", DocumentId = new DocumentId(documentId), ChunkIndex = 0 },
        Score = score,
    };

    private static MemoryRecord Rec(MemoryId id, string text) => new()
    {
        Id = id, OwnerId = "alice", Kind = MemoryKind.Fact, Text = text, CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task Same_id_from_two_partitions_is_one_hit_with_the_best_score_and_every_filter_carries_the_owner()
    {
        var id = MemoryId.New();
        var other = MemoryId.New();
        var agent = AgentId.New();
        var store = Substitute.For<IVectorStore>();
        var filters = new List<IDictionary<string, MetadataValue>>();
        store.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var filter = ci.Arg<SearchOptions>().MetadataFilter!;
                filters.Add(filter);
                var pinnedPartition = string.Equals(filter[RagNetMemoryIndex.AgentKey].StringValue, agent.ToString(), StringComparison.Ordinal);
                IReadOnlyList<SearchResult> results = pinnedPartition
                    ? [Hit(id.ToString(), 0.9), Hit(other.ToString(), 0.5), Hit("doc-1", 1.0)]
                    : [Hit(id.ToString(), 0.7)];
                return Task.FromResult(results);
            });

        var hits = (await Index(store).SearchAsync("q", new MemoryScope("alice", agent, "daedalus"), new MemorySearchOptions(10, 0), default)).Value;

        hits.Select(h => h.Id).Should().Equal(id, other);
        hits[0].Score.Should().Be(0.9, "the best partition score wins");
        hits.Should().NotContain(h => h.Score == 1.0, "a foreign document id is not a memory id");
        filters.Should().HaveCount(3, "(alice, agent), (alice, null), (daedalus, null)");
        filters.Should().OnlyContain(f => f[RagNetMemoryIndex.MarkerKey].StringValue == RagNetMemoryIndex.MarkerValue);
        filters.Select(f => (f[RagNetMemoryIndex.OwnerKey].StringValue, f[RagNetMemoryIndex.AgentKey].StringValue))
            .Should().Equal(("alice", agent.ToString()), ("alice", ""), ("daedalus", ""));
    }

    [Fact]
    public async Task Duplicate_ids_in_a_batch_are_embedded_and_stored_once_last_wins()
    {
        var store = Substitute.For<IVectorStore>();
        IReadOnlyList<EmbeddedChunk>? stored = null;
        store.StoreAsync(Arg.Do<IReadOnlyList<EmbeddedChunk>>(c => stored = c), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var id = MemoryId.New();

        (await Index(store).UpsertAsync([Rec(id, "first"), Rec(MemoryId.New(), "other"), Rec(id, "last")], default)).IsSuccess.Should().BeTrue();

        stored.Should().NotBeNull();
        stored!.Select(c => c.Chunk.Text).Should().Equal("other", "last");
    }
}
