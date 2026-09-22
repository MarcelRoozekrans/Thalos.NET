using System.Data.Async.Adapters;
using Npgsql;
using Thalos.Workflow;
using Thalos.Workflow.Orm;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox.Orm;

// A plain console app, deliberately: it is started as a brand-new OS process by
// DurabilityAcrossProcessesTests via Process.Start, with the first invocation fully exited before the
// second begins. Nothing here is shared in memory between "write" and "read" — the only channel between them
// is whatever "write" persisted to PostgreSQL. A same-process test (a second store instance in the same
// AppDomain) would pass even if a run's state were sitting in a field somewhere; this cannot.
if (args.Length < 2)
{
    await Console.Error.WriteLineAsync("usage: write <connString> | read <connString> <runId>");
    return 2;
}

var command = args[0];
var connectionString = args[1];
var ct = CancellationToken.None;

try
{
    return command switch
    {
        "write" => await WriteAsync(connectionString, ct).ConfigureAwait(false),
        "read" when args.Length >= 3 => await ReadAsync(connectionString, args[2], ct).ConfigureAwait(false),
        "read" => Fail("read requires a runId argument"),
        _ => Fail($"unknown command '{command}'"),
    };
}
catch (Exception ex)
{
    await Console.Error.WriteLineAsync(ex.ToString()).ConfigureAwait(false);
    return 1;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}

static async Task EnsureSchemaAsync(string connectionString, CancellationToken ct)
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(ct).ConfigureAwait(false);
    var asyncConnection = connection.AsAsync();

    await new MigrationRunner(asyncConnection, OutboxOrmMigrations.Postgres, new PostgresMigrationDialect())
        .RunAsync(ct).ConfigureAwait(false);
    await new MigrationRunner(asyncConnection, WorkflowOrmMigrations.Postgres, new PostgresMigrationDialect())
        .RunAsync(ct).ConfigureAwait(false);
}

static async Task<int> WriteAsync(string connectionString, CancellationToken ct)
{
    await EnsureSchemaAsync(connectionString, ct).ConfigureAwait(false);

    var store = new OrmWorkflowStore(new WorkflowOrmOptions { ConnectionString = connectionString, EnsureSchemaOnStartup = false });

    var runId = await store.StartAsync("manufacture", 1, Guid.NewGuid().ToString(), "implement", ct).ConfigureAwait(false);

    await store.CompleteNodeAsync(
        runId,
        seq: 1,
        new WorkflowTransition("review", WorkflowStatus.Running, null, WorkflowEventKind.Completed),
        new NodeResult("ok", new Dictionary<string, object?>(StringComparer.Ordinal)),
        ct).ConfigureAwait(false);

    Console.WriteLine(runId);
    return 0;
}

static async Task<int> ReadAsync(string connectionString, string runIdText, CancellationToken ct)
{
    if (!Guid.TryParse(runIdText, out var runId))
    {
        return Fail($"'{runIdText}' is not a valid run id");
    }

    var store = new OrmWorkflowStore(new WorkflowOrmOptions { ConnectionString = connectionString, EnsureSchemaOnStartup = false });
    var run = await store.FindAsync(runId, ct).ConfigureAwait(false);
    if (run is null)
    {
        return Fail($"run '{runId}' was not found");
    }

    Console.WriteLine($"current_node={run.CurrentNode}");
    Console.WriteLine($"status={run.Status}");
    Console.WriteLine($"current_seq={run.CurrentSeq}");
    return 0;
}
