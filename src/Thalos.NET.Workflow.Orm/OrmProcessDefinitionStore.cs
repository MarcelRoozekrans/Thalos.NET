using Npgsql;
using ZeroAlloc.Results;

namespace Thalos.Workflow.Orm;

/// <summary>
/// PostgreSQL-backed <see cref="IProcessDefinitionStore"/>: raw ADO.NET (no EF Core) over <c>process_definition</c>,
/// mirroring <see cref="OrmWorkflowStore"/>'s shape. Shares <see cref="WorkflowOrmOptions"/> with
/// <see cref="OrmWorkflowStore"/> — both open a connection against the same <see cref="WorkflowOrmOptions.ConnectionString"/>
/// per call, and <see cref="TryRemoveAsync"/> reads <c>workflow_run</c> directly, in the same database, to check
/// whether a run still pins the version being removed.
/// </summary>
public sealed class OrmProcessDefinitionStore(WorkflowOrmOptions options) : IProcessDefinitionStore
{
    private readonly WorkflowOrmOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc/>
    public async ValueTask UpsertAndActivateAsync(ProcessDefinition definition, string yaml, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(yaml);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText = """
                INSERT INTO process_definition (process, version, yaml, is_active)
                VALUES (@process, @version, @yaml, false)
                ON CONFLICT (process, version) DO UPDATE SET yaml = EXCLUDED.yaml
                """;
            upsert.Parameters.AddWithValue("process", definition.Name);
            upsert.Parameters.AddWithValue("version", definition.Version);
            upsert.Parameters.AddWithValue("yaml", yaml);
            await upsert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // One statement, keyed on the version just upserted: the new version becomes the process's only active
        // row and every other version of the same process is deactivated in the same UPDATE. No statement
        // ordering could leave two versions of one process active at once, even transiently within this
        // transaction — ix_process_definition_one_active_per_process enforces the same invariant at the schema
        // level as a second line of defence.
        await using (var activate = connection.CreateCommand())
        {
            activate.Transaction = tx;
            activate.CommandText = """
                UPDATE process_definition
                SET is_active = (version = @version)
                WHERE process = @process
                """;
            activate.Parameters.AddWithValue("process", definition.Name);
            activate.Parameters.AddWithValue("version", definition.Version);
            await activate.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<int?> GetActiveVersionAsync(string process, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(process);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT version FROM process_definition WHERE process = @process AND is_active";
        cmd.Parameters.AddWithValue("process", process);
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? null : (int)value;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Deliberately does not require a <c>process_definition</c> row to exist for <paramref name="version"/>:
    /// <see cref="IWorkflowStore.StartAsync"/> stamps <see cref="WorkflowRun.ProcessVersion"/> onto a run
    /// independently of this store, so a run can pin a version this store never held a row for (or has already
    /// removed the row of). Removing a version with no stored row is a no-op success, the same as removing an
    /// entry that was already gone — the property this method enforces is purely "does a non-terminal run still
    /// need this shape", never "does a row happen to exist".
    /// </remarks>
    public async ValueTask<Result> TryRemoveAsync(string process, int version, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(process);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var pinned = await CountPinningRunsAsync(connection, tx, process, version, ct).ConfigureAwait(false);
        if (pinned > 0)
        {
            return Result.Failure($"Process '{process}' version {version} is still pinned by {pinned} run(s) that have not reached a terminal status.");
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM process_definition WHERE process = @process AND version = @version";
            delete.Parameters.AddWithValue("process", process);
            delete.Parameters.AddWithValue("version", version);
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>
    /// The pin this store exists to enforce: a run started against this exact (process, version) pair keeps
    /// <see cref="WorkflowRun.ProcessVersion"/> fixed at that value for its whole life
    /// (<see cref="IWorkflowStore.StartAsync"/> captures it once and nothing ever changes it), so a run that has
    /// not yet reached a terminal status needs this shape resolvable for as long as it runs. Filtered on
    /// <c>process_version</c>, not just <c>process</c>: a run pinned to a <em>different</em> version of the same
    /// process must never block removing this one — dropping this filter is exactly the regression
    /// <c>Thalos.Tests.Workflow.Orm.ProcessDefinitionStoreTests</c> exercises.
    /// </summary>
    private static async Task<long> CountPinningRunsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string process, int version, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT count(*) FROM workflow_run
            WHERE process = @process AND process_version = @version
              AND status NOT IN (@succeeded, @failed, @cancelled)
            """;
        cmd.Parameters.AddWithValue("process", process);
        cmd.Parameters.AddWithValue("version", version);
        cmd.Parameters.AddWithValue("succeeded", nameof(WorkflowStatus.Succeeded));
        cmd.Parameters.AddWithValue("failed", nameof(WorkflowStatus.Failed));
        cmd.Parameters.AddWithValue("cancelled", nameof(WorkflowStatus.Cancelled));
        return (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }
}
