using Npgsql;
using Thalos.Workflow;
using Thalos.Workflow.Orm;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>Builds runs for the store's tests through the real store, so no test writes run rows by hand.</summary>
internal static class WorkflowRunSeeds
{
    public static OrmProcessDefinitionStore NewDefinitionStore(string connectionString) =>
        new(new WorkflowOrmOptions { ConnectionString = connectionString });

    public static async Task<Guid> StartAsync(OrmWorkflowStore store, string process, string correlation)
    {
        var started = await store.StartAsync(new WorkflowStartRequest
        {
            Process = process,
            Version = 1,
            CorrelationKey = correlation,
            StartNode = "implement",
            InitialVariables = null,
            StartedBy = TestPrincipals.Starter,
        }, CancellationToken.None);
        return started.Value;
    }

    public static async Task SetCreatedAtAsync(string connectionString, Guid runId, DateTimeOffset createdAt)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE workflow_run SET created_at = @at WHERE id = @id", connection);
        cmd.Parameters.AddWithValue("at", createdAt);
        cmd.Parameters.AddWithValue("id", runId);
        await cmd.ExecuteNonQueryAsync();
    }
}
