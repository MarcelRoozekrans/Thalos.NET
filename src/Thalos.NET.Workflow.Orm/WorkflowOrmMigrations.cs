using System.Reflection;
using ZeroAlloc.ORM.Migrations;

namespace Thalos.Workflow.Orm;

/// <summary>
/// The embedded SQL migrations that create <c>workflow_run</c> and <c>workflow_run_event</c>, mirroring
/// <c>ZeroAlloc.Outbox.Orm.OutboxOrmMigrations</c>'s shape. Apply with <see cref="MigrationRunner"/> against a
/// <c>PostgresMigrationDialect</c> — see <see cref="WorkflowOrmThalosBuilderExtensions.AddWorkflowOrm"/>, which
/// runs this alongside <c>OutboxOrmMigrations.Postgres</c> at startup when
/// <see cref="WorkflowOrmOptions.EnsureSchemaOnStartup"/> is set.
/// </summary>
/// <remarks>
/// The migrations embedded here start at version 1000 deliberately: <c>MigrationRunner</c>'s history table
/// (<c>__zaorm_migrations</c>) is a single sequence shared by every migration source applied against the same
/// database, keyed on <c>version</c> alone with no per-source namespace. <c>ZeroAlloc.Outbox.Orm</c>'s own
/// migration claims version 1. A version collision is not rejected — it is silently treated as "already
/// applied" and the colliding migration's SQL never runs — so this package reserves the 1000+ range to stay
/// clear of the outbox's low numbers and of whatever range a future migration source picks next.
/// <para>
/// <b>Rolling deploys: migration 1004 is not backward compatible with pre-1004 code.</b> It adds
/// <c>process_definition.content_hash</c> as <c>NOT NULL</c> with no default. PostgreSQL validates <c>NOT NULL</c>
/// against the proposed tuple <em>before</em> conflict resolution, so an instance running code that predates 1004
/// — whose INSERT never mentions the column — fails with <c>23502</c> on every
/// <see cref="IProcessDefinitionStore.UpsertAndActivateAsync"/> call, including the <c>ON CONFLICT</c> path.
/// Applying 1004 ahead of the rollout therefore breaks process syncing on every instance still on the old code,
/// for as long as it is still running. Apply the migration and deploy the matching code as one step.
/// </para>
/// </remarks>
public static class WorkflowOrmMigrations
{
    /// <summary>The workflow schema's migrations, for a PostgreSQL target.</summary>
    public static IMigrationSource Postgres { get; } =
        new EmbeddedResourceMigrationSource(typeof(WorkflowOrmMigrations).GetTypeInfo().Assembly, "Thalos.Workflow.Orm.Migrations.");
}
