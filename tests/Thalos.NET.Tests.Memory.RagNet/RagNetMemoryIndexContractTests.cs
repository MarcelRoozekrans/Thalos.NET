using Microsoft.Extensions.AI;
using Rag.NET.PgVector;
using Thalos.Memory;
using Thalos.Memory.RagNet;
using Thalos.Testing;

namespace Thalos.Tests.Memory.RagNet;

[Collection(PgVectorCollection.Name)]
[Trait("Category", "Docker")]
public sealed class RagNetMemoryIndexContractTests(PgVectorFixture pg) : MemoryIndexContractTests, IDisposable
{
    private readonly List<PgVectorStore> _stores = [];

    // Drops and recreates rag_chunks: the contract suite calls this exactly once per test (documented on MemoryIndexContractTests).
    protected override async ValueTask<IMemoryIndex> CreateIndexAsync(IEmbeddingGenerator<string, Embedding<float>> embeddings)
    {
        var store = new PgVectorStore(pg.ConnectionString, Dimensions);
        _stores.Add(store);
        await pg.ResetAsync();
        await store.InitializeAsync();
        return new RagNetMemoryIndex(store, embeddings, new RagNetMemoryOptions { ConnectionString = pg.ConnectionString, VectorDimensions = Dimensions });
    }

    public void Dispose()
    {
        foreach (var s in _stores) { s.Dispose(); }
    }
}
