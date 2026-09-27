using Npgsql;
using Thalos.Workflow.Orm;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>
/// <see cref="WorkflowOrmSchemaInitializer"/> against a database it has never seen. The collection's own database
/// already carries the schema <see cref="PostgresFixture"/> applied, so this creates a fresh one in the same
/// container: only there is the initializer the thing that put each column in place.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Docker")]
public sealed class WorkflowOrmSchemaInitializerTests(PostgresFixture pg)
{
    [Fact]
    public async Task Initializer_gives_the_outbox_table_the_lease_columns_the_outbox_claims_through()
    {
        var database = $"init_{Guid.NewGuid():N}";
        await ExecuteAsync(pg.ConnectionString, $"CREATE DATABASE {database}");
        var connectionString = new NpgsqlConnectionStringBuilder(pg.ConnectionString) { Database = database }.ConnectionString;
        try
        {
            await new WorkflowOrmSchemaInitializer(new WorkflowOrmOptions { ConnectionString = connectionString })
                .StartAsync(CancellationToken.None);

            var columns = new List<string>();
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var select = new NpgsqlCommand(
                    "SELECT column_name FROM information_schema.columns WHERE table_name = 'outboxmessages'", connection);
                await using var reader = await select.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    columns.Add(reader.GetString(0));
                }
            }

            columns.Should().Contain(["lockedby", "lockeduntil"], "ZeroAlloc.Outbox 3.0 claims and marks through LockedBy and LockedUntil");
        }
        finally
        {
            await ExecuteAsync(pg.ConnectionString, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}
