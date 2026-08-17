using Microsoft.Extensions.AI;
using Npgsql;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Thalos.Memory;
using Thalos.Memory.RagNet;
using Thalos.Testing;

namespace Thalos.Tests.Memory.RagNet;

public sealed class RagNetErrorMappingTests
{
    private static readonly MemoryRecord Rec = new() { Id = MemoryId.New(), OwnerId = "alice", Kind = MemoryKind.Fact, Text = "x", CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch };

    private static RagNetMemoryIndex Index(IVectorStore store, IEmbeddingGenerator<string, Embedding<float>>? gen = null, int dims = 64) =>
        new(store, gen ?? new HashedBagOfWordsEmbeddingGenerator(dims), new RagNetMemoryOptions { ConnectionString = "Host=x", VectorDimensions = dims });

    [Fact]
    public async Task PostgresException_maps_to_MemoryIndexFailed_with_sql_state()
    {
        var store = Substitute.For<IVectorStore>();
        store.StoreAsync(Arg.Any<IReadOnlyList<EmbeddedChunk>>(), Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new PostgresException("relation \"rag_chunks\" does not exist", "ERROR", "ERROR", "42P01"));
        var r = await Index(store).UpsertAsync([Rec], default);
        r.Error.Code.Should().Be(AgentErrorCode.MemoryIndexFailed);
        r.Error.Detail.Should().Be("42P01");
        r.Error.ToString().Should().NotContain("rag_chunks");
    }

    [Fact]
    public async Task Other_exceptions_map_to_MemoryIndexUnavailable_with_type_name()
    {
        var store = Substitute.For<IVectorStore>();
        store.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<SearchResult>>>(_ => throw new NpgsqlException("connection refused"));
        var r = await Index(store).SearchAsync("q", new MemoryScope("alice", null), new MemorySearchOptions(5, 0), default);
        r.Error.Code.Should().Be(AgentErrorCode.MemoryIndexUnavailable);
        r.Error.Detail.Should().Be(nameof(NpgsqlException));
    }

    [Fact]
    public async Task Probe_reports_dimensions_and_flags_a_mismatch()
    {
        var store = Substitute.For<IVectorStore>();
        store.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));
        var ok = (await Index(store, dims: 64).ProbeAsync(default)).Value;
        ok.Available.Should().BeTrue();
        ok.Dimensions.Should().Be(64);

        var mismatch = (await Index(store, new HashedBagOfWordsEmbeddingGenerator(32), dims: 64).ProbeAsync(default)).Value;
        mismatch.Available.Should().BeFalse();
        mismatch.Dimensions.Should().Be(32);
        mismatch.Detail.Should().Contain("32").And.Contain("64");
    }

    [Fact]
    public async Task Probe_reports_unavailable_when_the_store_throws()
    {
        var store = Substitute.For<IVectorStore>();
        store.SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<SearchResult>>>(_ => throw new PostgresException("no table", "ERROR", "ERROR", "42P01"));
        var health = await Index(store).ProbeAsync(default);
        health.IsSuccess.Should().BeTrue();
        health.Value.Available.Should().BeFalse();
        health.Value.Detail.Should().Be("42P01");
    }
}
