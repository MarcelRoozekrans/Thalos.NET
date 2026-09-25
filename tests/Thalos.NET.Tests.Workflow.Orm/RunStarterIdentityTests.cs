using Npgsql;
using Thalos.Workflow;
using Thalos.Workflow.Orm;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>
/// Every start names its starter (ruling R26), and a caller may supply the run's own id so it can prepare a
/// resource keyed by that id — such as a git worktree — before the first node is ever dispatched (ruling R27).
/// This suite proves both round-trip through <see cref="OrmWorkflowStore"/>, that <see cref="WorkflowRun.StartedBy"/>
/// is write-once like <see cref="WorkflowRun.Manifest"/>, and that a start with no starter is refused before any
/// row is written.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Docker")]
public sealed class RunStarterIdentityTests(PostgresFixture pg) : IAsyncLifetime
{
    private OrmWorkflowStore _store = null!;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        _store = NewStore();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Red if <c>started_by</c> is left out of <c>SelectRunSql</c> — <c>StartedBy</c> would then read back
    /// <see langword="null"/> regardless of what <c>StartAsync</c> wrote. Also red if <c>InsertRunAsync</c>
    /// ignores <see cref="WorkflowStartRequest.RunId"/> and keeps minting its own <see cref="Guid.NewGuid"/> — the
    /// caller-supplied id is what lets a host create the run's workspace before the first node is dispatched.
    /// </summary>
    [Fact]
    public async Task The_starter_and_the_caller_supplied_id_round_trip()
    {
        var runId = Guid.NewGuid();
        var id = await _store.StartAsync(new WorkflowStartRequest
        {
            Process = "p",
            Version = 1,
            CorrelationKey = $"k:{Guid.NewGuid()}",
            StartNode = "a",
            RunId = runId,
            StartedBy = new RunPrincipal("user-1", ["admin", "analyst"]) { DisplayName = "admin" },
        }, CancellationToken.None);

        id.Should().Be(runId, "the host creates the workspace under this id before the run row exists");
        var run = await _store.FindAsync(id, CancellationToken.None);
        run!.StartedBy.Should().BeEquivalentTo(new RunPrincipal("user-1", ["admin", "analyst"]) { DisplayName = "admin" });
    }

    /// <summary>
    /// Red if <c>UpdateRunAsync</c> ever lists <c>started_by</c> among the columns it sets — deliberately
    /// introduced as <c>started_by = COALESCE(@variables::jsonb -> 'started_by', started_by)</c> and reverted to
    /// watch this assertion fail; see the task report. A node's own outcome report must never be able to rewrite
    /// who started the run, even when it reports a variable literally named <c>started_by</c> or <c>StartedBy</c>.
    /// </summary>
    [Fact]
    public async Task A_node_reporting_a_started_by_variable_never_changes_the_recorded_starter()
    {
        var id = await _store.StartAsync(new WorkflowStartRequest
        {
            Process = "p",
            Version = 1,
            CorrelationKey = $"k:{Guid.NewGuid()}",
            StartNode = "a",
            StartedBy = new RunPrincipal("user-1", ["developer"]),
        }, CancellationToken.None);
        var run = await _store.FindAsync(id, CancellationToken.None);

        await _store.CompleteNodeAsync(
            id, run!.CurrentSeq,
            new WorkflowTransition("b", WorkflowStatus.Running, null, WorkflowEventKind.Completed),
            new NodeResult("ok", new Dictionary<string, object?>(StringComparer.Ordinal) { ["started_by"] = "attacker", ["StartedBy"] = "attacker" }),
            CancellationToken.None);

        (await _store.FindAsync(id, CancellationToken.None))!.StartedBy!.Id.Should().Be("user-1");
    }

    /// <summary>
    /// Red if <c>InsertRunAsync</c> writes <c>started_by</c> only inside an <c>if (request.RunId is not null)</c>
    /// branch — deliberately introduced and reverted to watch this assertion fail; see the task report. A run
    /// started with no caller-supplied id must still record its starter; <see cref="WorkflowStartRequest.RunId"/>
    /// and <see cref="WorkflowStartRequest.StartedBy"/> are independent, and the store must not couple them.
    /// </summary>
    [Fact]
    public async Task Without_a_run_id_the_store_generates_one_and_still_records_the_starter()
    {
        var id = await _store.StartAsync(new WorkflowStartRequest
        {
            Process = "p",
            Version = 1,
            CorrelationKey = $"k:{Guid.NewGuid()}",
            StartNode = "a",
            StartedBy = TestPrincipals.Starter,
        }, CancellationToken.None);

        id.Should().NotBe(Guid.Empty);
        (await _store.FindAsync(id, CancellationToken.None))!.StartedBy!.Id.Should().Be("test-starter");
    }

    /// <summary>
    /// Red if the <see cref="WorkflowStartRequest.StartedBy"/> guard is deleted from <c>OrmWorkflowStore.StartAsync</c>
    /// — deliberately deleted and reverted to watch this assertion fail; see the task report. <c>required</c>
    /// alone does not stop a caller from passing <c>StartedBy = null!</c>, so the guard is the runtime half of
    /// ruling R26, and a rejected start must not have written a row.
    /// </summary>
    [Fact]
    public async Task A_start_with_no_starter_is_refused_and_writes_no_row()
    {
        var key = $"k:{Guid.NewGuid()}";
        var act = async () => await _store.StartAsync(new WorkflowStartRequest
        {
            Process = "p",
            Version = 1,
            CorrelationKey = key,
            StartNode = "a",
            StartedBy = null!,
        }, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
        (await CountRunsWithCorrelationKeyAsync(key)).Should().Be(0);
    }

    private OrmWorkflowStore NewStore() =>
        new(new WorkflowOrmOptions { ConnectionString = pg.ConnectionString }, NewDefinitionStore());

    private OrmProcessDefinitionStore NewDefinitionStore() =>
        new(new WorkflowOrmOptions { ConnectionString = pg.ConnectionString });

    private async Task<long> CountRunsWithCorrelationKeyAsync(string correlationKey)
    {
        await using var connection = new NpgsqlConnection(pg.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM workflow_run WHERE correlation_key = @key", connection);
        cmd.Parameters.AddWithValue("key", correlationKey);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
