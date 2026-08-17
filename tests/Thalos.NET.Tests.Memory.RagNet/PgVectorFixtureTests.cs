using Rag.NET.PgVector;

namespace Thalos.Tests.Memory.RagNet;

[Collection(PgVectorCollection.Name)]
[Trait("Category", "Docker")]
public sealed class PgVectorFixtureTests(PgVectorFixture pg)
{
    [Fact]
    public async Task Store_initializes_rag_chunks_idempotently()
    {
        using var store = new PgVectorStore(pg.ConnectionString, 128);
        await store.InitializeAsync();
        await store.InitializeAsync();
        await pg.ResetAsync();
    }
}
