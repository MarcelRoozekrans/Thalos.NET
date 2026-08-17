using Microsoft.Extensions.AI;
using Thalos.Memory;
using Thalos.Testing;

namespace Thalos.Tests.Memory;

public sealed class InMemoryMemoryIndexTests : MemoryIndexContractTests
{
    protected override ValueTask<IMemoryIndex> CreateIndexAsync(IEmbeddingGenerator<string, Embedding<float>> embeddings) => new(new InMemoryMemoryIndex(embeddings));

    [Fact]
    public async Task Generator_failure_maps_to_MemoryIndexUnavailable_without_exception_text()
    {
        var index = new InMemoryMemoryIndex(new ThrowingGenerator());
        var r = await index.UpsertAsync([Rec("alice", null, "x")], CancellationToken.None);
        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be(AgentErrorCode.MemoryIndexUnavailable);
        r.Error.Detail.Should().Be(nameof(HttpRequestException));
        r.Error.ToString().Should().NotContain("connection refused");
    }

    [Fact]
    public async Task Unavailable_index_reports_unavailable_and_fails_upsert_and_search()
    {
        var index = UnavailableMemoryIndex.Instance;
        (await index.ProbeAsync(CancellationToken.None)).Value.Available.Should().BeFalse();
        (await index.UpsertAsync([Rec("alice", null, "x")], CancellationToken.None)).Error.Code.Should().Be(AgentErrorCode.MemoryIndexUnavailable);
        (await index.SearchAsync("x", new MemoryScope("alice", null), new MemorySearchOptions(5, 0), CancellationToken.None)).Error.Code.Should().Be(AgentErrorCode.MemoryIndexUnavailable);
        (await index.RemoveAsync(MemoryId.New(), CancellationToken.None)).IsSuccess.Should().BeTrue();
    }

    private sealed class ThrowingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) => throw new HttpRequestException("connection refused");
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
