using System.Data.Async;
using ZeroAlloc.Outbox;

namespace Thalos.Workflow.Orm;

/// <summary>Configures <see cref="OrmWorkflowStore"/> and the schema it depends on.</summary>
public sealed class WorkflowOrmOptions
{
    private readonly Dictionary<(string Process, int Version), ProcessDefinition> _processes = [];

    /// <summary>The PostgreSQL connection string <see cref="OrmWorkflowStore"/> opens a connection against per call.</summary>
    public required string ConnectionString { get; set; }

    /// <summary>
    /// When set (the default), a hosted service runs <see cref="WorkflowOrmMigrations.Postgres"/> and
    /// <c>ZeroAlloc.Outbox.Orm.OutboxOrmMigrations.Postgres</c> at startup, before the host accepts work.
    /// </summary>
    public bool EnsureSchemaOnStartup { get; set; } = true;

    /// <summary>
    /// The process definitions <see cref="OrmWorkflowStore.ResumeAsync"/> resolves a run's <c>(Process, ProcessVersion)</c>
    /// against to call <see cref="WorkflowInterpreter.Advance"/>. A run whose process is not registered here fails to resume.
    /// </summary>
    internal IReadOnlyDictionary<(string Process, int Version), ProcessDefinition> Processes => _processes;

    /// <summary>
    /// Test-only seam: overrides how <see cref="OrmWorkflowStore"/> obtains the <c>IOutboxStore</c> it enqueues the
    /// post-transition dispatch message through. <see langword="null"/> (the default) uses
    /// <c>ZeroAlloc.Outbox.Orm.OrmOutboxStore</c> against the same connection as the rest of the transaction.
    /// </summary>
    internal Func<IAsyncDbConnection, IOutboxStore>? OutboxStoreFactory { get; set; }

    /// <summary>Registers <paramref name="definition"/> so runs of <c>(definition.Name, definition.Version)</c> can resume.</summary>
    public WorkflowOrmOptions AddProcess(ProcessDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _processes[(definition.Name, definition.Version)] = definition;
        return this;
    }
}
