using Npgsql;
using Thalos.Workflow;
using Thalos.Workflow.Orm;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>Builds runs for the store's tests through the real store, so no test writes run rows by hand.</summary>
internal static class WorkflowRunSeeds
{
    /// <summary>The signal the gate in <see cref="ApprovalYaml"/> awaits.</summary>
    public const string GateSignal = "ok";

    /// <summary>A task node, an approval gate awaiting <see cref="GateSignal"/>, a terminal.</summary>
    public const string ApprovalYaml = $$"""
        process: approval
        version: 1
        nodes:
          start: { next: gate }
          gate: { await: {{GateSignal}}, next: done }
          done: { terminal: succeeded }
        """;

    /// <summary>A task node, then an action node: the shape a retry is for.</summary>
    public const string PublishingYaml = """
        process: publishing
        version: 1
        nodes:
          start: { next: publish }
          publish: { action: open-pull-request, outcomes: [published], next: done }
          done: { terminal: succeeded }
        """;

    private static readonly IReadOnlyDictionary<string, object?> Empty = new Dictionary<string, object?>(StringComparer.Ordinal);

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

    /// <summary>
    /// Writes <paramref name="yaml"/> into <c>process_definition</c> and activates it, and returns a definition store
    /// over that table. A definition has to be in the table for a resume or a retry to find it.
    /// </summary>
    public static async Task<OrmProcessDefinitionStore> ActivateAsync(PostgresFixture pg, string yaml)
    {
        var definitions = NewDefinitionStore(pg.ConnectionString);
        var definition = ProcessLoader.Load(yaml);
        definition.IsSuccess.Should().BeTrue(definition.IsFailure ? definition.Error : "");
        (await definitions.UpsertAndActivateAsync(definition.Value, yaml, CancellationToken.None)).IsSuccess.Should().BeTrue();
        return definitions;
    }

    /// <summary>
    /// Activates <see cref="ApprovalYaml"/>, starts an approval run, and parks it at <c>gate</c> awaiting
    /// <see cref="GateSignal"/>. Returns the run id.
    /// </summary>
    public static async Task<Guid> ParkedAtGateAsync(OrmWorkflowStore store, PostgresFixture pg, string correlation = "c-gate")
    {
        await ActivateAsync(pg, ApprovalYaml);
        var runId = (await store.StartAsync(new WorkflowStartRequest { Process = "approval", Version = 1, CorrelationKey = correlation, StartNode = "start", InitialVariables = null, StartedBy = TestPrincipals.Starter }, CancellationToken.None)).Value;
        await store.CompleteNodeAsync(
            runId, seq: 1,
            new WorkflowTransition("gate", WorkflowStatus.Awaiting, GateSignal, WorkflowEventKind.Awaiting),
            new NodeResult(null, Empty), CancellationToken.None);
        return runId;
    }

    /// <summary>
    /// Activates <see cref="PublishingYaml"/>, starts a publishing run, moves it onto <c>publish</c> at seq 2, and
    /// fails it there. Returns the run id, the failed node and its seq, as <c>RetryFailedNodeAsync</c> expects them.
    /// </summary>
    public static async Task<(Guid RunId, string Node, long Seq)> FailedAtHostActionAsync(
        OrmWorkflowStore store, PostgresFixture pg, string correlation = "c-retry")
    {
        await ActivateAsync(pg, PublishingYaml);
        var runId = (await store.StartAsync(new WorkflowStartRequest { Process = "publishing", Version = 1, CorrelationKey = correlation, StartNode = "start", InitialVariables = new Dictionary<string, object?>(StringComparer.Ordinal) { ["work_intent"] = "x" }, StartedBy = TestPrincipals.Starter }, CancellationToken.None)).Value;
        await store.CompleteNodeAsync(runId, seq: 1, new WorkflowTransition("publish", WorkflowStatus.Running, null, WorkflowEventKind.Completed), new NodeResult(null, Empty), CancellationToken.None);
        await store.FailAsync(runId, "node 'publish': push failed", CancellationToken.None);
        return (runId, "publish", 2);
    }
}
