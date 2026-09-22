using Npgsql;
using Thalos.Workflow;
using Thalos.Workflow.Orm;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>
/// Tests for Task 7's stranded-run sweep. The property that decides whether this sweep is correct is not that
/// it terminates stale runs — that half is easy — but that it leaves healthy ones alone. An agent turn
/// legitimately takes minutes, so <see cref="ProductionThreshold"/> is chosen well above that: 30 minutes is
/// comfortably past any single node's expected agent-turn duration and past the outbox's own retry-and-backoff
/// window for a message that is going to dead-letter, so a run this sweep terminates has genuinely gone
/// silent, not merely taken a normal-length turn.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Docker")]
public sealed class WorkflowRunReconcilerTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly IReadOnlyDictionary<string, object?> Empty = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>
    /// The threshold used by every test in this class except the falsifiability check. See this class's
    /// remarks for why 30 minutes: it must exceed the longest expected node duration (an agent turn
    /// legitimately takes minutes), or the sweep would terminate perfectly healthy in-flight work.
    /// </summary>
    private static readonly TimeSpan ProductionThreshold = TimeSpan.FromMinutes(30);

    private OrmWorkflowStore _store = null!;
    private WorkflowRunReconciler _reconciler = null!;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        _store = new OrmWorkflowStore(new WorkflowOrmOptions { ConnectionString = pg.ConnectionString });
        _reconciler = new WorkflowRunReconciler(_store);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SweepAsync_terminates_a_running_run_stale_past_the_threshold_with_a_named_reason()
    {
        var runId = await _store.StartAsync("manufacture", 1, "c-stranded", "implement", CancellationToken.None);
        await BackdateUpdatedAtAsync(runId, TimeSpan.FromHours(1));

        var terminated = await _reconciler.SweepAsync(ProductionThreshold, CancellationToken.None);

        terminated.Should().Be(1);
        var run = await _store.FindAsync(runId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Failed, "a run with nothing in flight and nothing left to advance it must be terminated, not left Running forever");
        run.LastError.Should().Contain("stranded", "the reason recorded against the run must name why it was terminated, not just that it was");
        run.LastError.Should().Contain(runId.ToString());
    }

    [Fact]
    public async Task SweepAsync_leaves_a_recently_updated_running_run_alone()
    {
        // This is the assertion that stops the sweep from killing healthy long-running nodes: a run updated a
        // single second ago is well within a normal agent turn and must not be touched, even though it is
        // Running and would otherwise match FindStrandedAsync's status filter.
        var runId = await _store.StartAsync("manufacture", 1, "c-healthy", "implement", CancellationToken.None);
        await BackdateUpdatedAtAsync(runId, TimeSpan.FromSeconds(1));

        var terminated = await _reconciler.SweepAsync(ProductionThreshold, CancellationToken.None);

        terminated.Should().Be(0, "a run updated one second ago is healthy in-flight work, not a stranded run");
        var run = await _store.FindAsync(runId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Running);
        run.LastError.Should().BeNull();
    }

    [Fact]
    public async Task SweepAsync_leaves_a_stale_awaiting_run_alone()
    {
        // A run parked at an approval gate has nothing in flight by design and may legitimately sit there for
        // days — terminating it because nobody approved it quickly would destroy exactly the work the gate
        // exists to protect. Parked directly via CompleteNodeAsync (no process definition needed for this
        // store call) rather than through a full gate node, mirroring OrmWorkflowStoreTests' own style.
        var runId = await _store.StartAsync("approval", 1, "c-awaiting", "start", CancellationToken.None);
        var toGate = new WorkflowTransition("gate", WorkflowStatus.Awaiting, "ok", WorkflowEventKind.Awaiting);
        await _store.CompleteNodeAsync(runId, seq: 1, toGate, new NodeResult(null, Empty), CancellationToken.None);
        await BackdateUpdatedAtAsync(runId, TimeSpan.FromDays(10));

        var terminated = await _reconciler.SweepAsync(ProductionThreshold, CancellationToken.None);

        terminated.Should().Be(0, "a run awaiting a signal is not stranded — it is parked by design and must never be swept");
        var run = await _store.FindAsync(runId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Awaiting);
        run.AwaitingSignal.Should().Be("ok");
    }

    [Fact]
    public async Task SweepAsync_terminates_several_stranded_runs_in_one_call()
    {
        var first = await _store.StartAsync("manufacture", 1, "c-multi-1", "implement", CancellationToken.None);
        var second = await _store.StartAsync("manufacture", 1, "c-multi-2", "implement", CancellationToken.None);
        await BackdateUpdatedAtAsync(first, TimeSpan.FromHours(2));
        await BackdateUpdatedAtAsync(second, TimeSpan.FromHours(3));

        var terminated = await _reconciler.SweepAsync(ProductionThreshold, CancellationToken.None);

        terminated.Should().Be(2);
        (await _store.FindAsync(first, CancellationToken.None))!.Status.Should().Be(WorkflowStatus.Failed);
        (await _store.FindAsync(second, CancellationToken.None))!.Status.Should().Be(WorkflowStatus.Failed);
    }

    private async Task BackdateUpdatedAtAsync(Guid runId, TimeSpan age)
    {
        await using var connection = new NpgsqlConnection(pg.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE workflow_run SET updated_at = now() - @age WHERE id = @id", connection);
        cmd.Parameters.AddWithValue("age", age);
        cmd.Parameters.AddWithValue("id", runId);
        await cmd.ExecuteNonQueryAsync();
    }
}
