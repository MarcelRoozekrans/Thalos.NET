using Npgsql;
using Testcontainers.PostgreSql;

namespace Thalos.Tests.Memory.RagNet;

/// <summary>
/// One pgvector container per test collection; tests call <see cref="ResetAsync"/> (DROP TABLE rag_chunks) before <c>InitializeAsync</c> so every
/// test starts from an empty table at its own vector dimension whatever ran before it (test classes use 64 and 128; Rag.NET refuses to
/// initialise over a table of another dimension). Requires Docker (Linux containers) — exclude with --filter Category!=Docker.
/// </summary>
public sealed class PgVectorFixture : IAsyncLifetime
{
    public const string Image = "pgvector/pgvector:pg16";

#pragma warning disable CS0618 // PostgreSqlBuilder(): obsolete parameterless ctor in Testcontainers 4.x — same usage as Daedalus
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage(Image).Build();
#pragma warning restore CS0618

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        try
        {
            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Docker with Linux containers is required for the pgvector tests (image {Image}). Run without them: dotnet test --filter \"Category!=Docker\"", ex);
        }
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task ResetAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DROP TABLE IF EXISTS rag_chunks", conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

#pragma warning disable CA1711 // "Collection" suffix is the xUnit collection-definition naming convention, not a collection type
[CollectionDefinition(Name)]
public sealed class PgVectorCollection : ICollectionFixture<PgVectorFixture>
{
    public const string Name = "pgvector";
}
#pragma warning restore CA1711
