using System.Data.Async;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.Orm;

namespace Thalos.Workflow.Orm;

/// <summary>
/// Configures <see cref="OrmWorkflowStore"/> and the schema it depends on.
/// </summary>
/// <remarks>
/// Deliberately holds no process definitions. <see cref="OrmWorkflowStore.ResumeAsync"/> resolves a run's
/// definition through <see cref="IProcessDefinitionStore"/> — the same store <c>ProcessDefinitionSync</c> writes
/// to and <c>WorkflowNodeDispatcher</c> reads from. An <c>AddProcess</c> registry here was a second source of
/// truth for the same fact: a definition synced from git was not runnable unless a host also remembered to
/// register it in memory, and a definition registered here was runnable whether or not it had ever been synced
/// or validated. There is now exactly one answer to "what is this process", and it is the table.
/// </remarks>
public sealed class WorkflowOrmOptions
{
    /// <summary>The PostgreSQL connection string <see cref="OrmWorkflowStore"/> opens a connection against per call.</summary>
    public required string ConnectionString { get; set; }

    /// <summary>
    /// When set (the default), a hosted service runs <see cref="WorkflowOrmMigrations.Postgres"/> and
    /// <c>ZeroAlloc.Outbox.Orm.OutboxOrmMigrations.Postgres</c> at startup, before the host accepts work.
    /// </summary>
    /// <remarks>
    /// Leaving this on means the <em>first</em> instance to start applies any pending migration while the others
    /// are still running whatever code they were deployed with. That is fine for additive migrations and not fine
    /// for migration 1004, which pre-1004 code cannot write against at all — see
    /// <see cref="WorkflowOrmMigrations"/>. A deployment that rolls instances one at a time across that migration
    /// should apply schema changes as an explicit step with this turned off, rather than letting whichever
    /// instance happens to win the race decide when the rest start failing.
    /// </remarks>
    public bool EnsureSchemaOnStartup { get; set; } = true;

    /// <summary>
    /// How <see cref="OrmWorkflowStore"/> obtains the <c>IOutboxStore</c> it enqueues the post-transition dispatch
    /// message through, over the same connection as the rest of the transaction. The default is
    /// <see cref="CreatePostgresOutboxStore"/>; replacing it is a test-only seam.
    /// </summary>
    internal Func<IAsyncDbConnection, IOutboxStore> OutboxStoreFactory { get; set; } = CreatePostgresOutboxStore;

    /// <summary>
    /// <c>ZeroAlloc.Outbox.Orm.OrmOutboxStore</c> with <see cref="OutboxOrmDialect.Postgres"/>. The store's
    /// one-argument constructor selects <see cref="OutboxOrmDialect.Sqlite"/>, whose batch claim takes no
    /// <c>FOR UPDATE SKIP LOCKED</c>, so against PostgreSQL a claimer blocks on rows another claimer holds instead
    /// of passing over them. This package only enqueues through the store, which no dialect changes, but the store
    /// it builds should never carry a claim spelled for a different database.
    /// </summary>
    private static OrmOutboxStore CreatePostgresOutboxStore(IAsyncDbConnection connection) =>
        new(connection, OutboxOrmDialect.Postgres);
}
