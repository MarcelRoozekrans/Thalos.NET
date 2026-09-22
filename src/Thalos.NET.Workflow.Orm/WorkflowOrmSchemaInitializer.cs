using System.Data.Async.Adapters;
using Microsoft.Extensions.Hosting;
using Npgsql;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox.Orm;

namespace Thalos.Workflow.Orm;

/// <summary>
/// Runs <c>OutboxOrmMigrations.Postgres</c> and then <see cref="WorkflowOrmMigrations.Postgres"/> against
/// <see cref="WorkflowOrmOptions.ConnectionString"/> at startup, before the host accepts work. The outbox schema
/// runs first because <see cref="OrmWorkflowStore.StartAsync"/> and <see cref="OrmWorkflowStore.CompleteNodeAsync"/>
/// both enqueue into it in the same transaction as the workflow tables they write — the very first thing a new run
/// does is write to both, so neither can be missing.
/// </summary>
internal sealed class WorkflowOrmSchemaInitializer(WorkflowOrmOptions options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var asyncConnection = connection.AsAsync();

        var outboxRunner = new MigrationRunner(asyncConnection, OutboxOrmMigrations.Postgres, new PostgresMigrationDialect());
        await outboxRunner.RunAsync(cancellationToken).ConfigureAwait(false);

        var workflowRunner = new MigrationRunner(asyncConnection, WorkflowOrmMigrations.Postgres, new PostgresMigrationDialect());
        await workflowRunner.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
