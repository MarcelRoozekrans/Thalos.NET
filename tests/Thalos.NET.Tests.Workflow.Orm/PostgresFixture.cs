using System.Data.Async.Adapters;
using Npgsql;
using Testcontainers.PostgreSql;
using Thalos.Workflow.Orm;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox.Orm;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>
/// One PostgreSQL container per test collection, schema applied once in <see cref="InitializeAsync"/>; tests
/// call <see cref="ResetAsync"/> (truncate <c>workflow_run_event</c>, <c>workflow_run</c>, <c>outboxmessages</c>)
/// before each test so every test starts from empty tables. Mirrors
/// <c>Thalos.Tests.Memory.RagNet.PgVectorFixture</c>'s shape. Requires Docker (Linux containers) — exclude with
/// <c>--filter Category!=Docker</c>.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string Image = "postgres:16";

#pragma warning disable CS0618 // PostgreSqlBuilder(): obsolete parameterless ctor in Testcontainers 4.x — same usage as Daedalus / PgVectorFixture
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
            throw new InvalidOperationException($"Docker with Linux containers is required for the workflow ORM tests (image {Image}). Run without them: dotnet test --filter \"Category!=Docker\"", ex);
        }

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        var asyncConnection = connection.AsAsync();

        // Outbox schema first: OrmWorkflowStore.CompleteNodeAsync enqueues into it in the same transaction
        // as the workflow tables that depend on it being present.
        await new MigrationRunner(asyncConnection, OutboxOrmMigrations.Postgres, new PostgresMigrationDialect()).RunAsync(CancellationToken.None);
        await new MigrationRunner(asyncConnection, WorkflowOrmMigrations.Postgres, new PostgresMigrationDialect()).RunAsync(CancellationToken.None);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task ResetAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("TRUNCATE workflow_run_event, workflow_run, outboxmessages RESTART IDENTITY CASCADE", connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

#pragma warning disable CA1711 // "Collection" suffix is the xUnit collection-definition naming convention, not a collection type
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "workflow-postgres";
}
#pragma warning restore CA1711
