using Npgsql;
using Thalos.Workflow;
using Thalos.Workflow.Orm;
using ZeroAlloc.Results;

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
        var options = new WorkflowOrmOptions { ConnectionString = pg.ConnectionString };
        _store = new OrmWorkflowStore(options, new OrmProcessDefinitionStore(options));
        _reconciler = new WorkflowRunReconciler(_store);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SweepAsync_terminates_a_running_run_stale_past_the_threshold_with_a_named_reason()
    {
        var runId = await _store.StartAsync("manufacture", 1, "c-stranded", "implement", initialVariables: null, CancellationToken.None);
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
        var runId = await _store.StartAsync("manufacture", 1, "c-healthy", "implement", initialVariables: null, CancellationToken.None);
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
        var runId = await _store.StartAsync("approval", 1, "c-awaiting", "start", initialVariables: null, CancellationToken.None);
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
        var first = await _store.StartAsync("manufacture", 1, "c-multi-1", "implement", initialVariables: null, CancellationToken.None);
        var second = await _store.StartAsync("manufacture", 1, "c-multi-2", "implement", initialVariables: null, CancellationToken.None);
        await BackdateUpdatedAtAsync(first, TimeSpan.FromHours(2));
        await BackdateUpdatedAtAsync(second, TimeSpan.FromHours(3));

        var terminated = await _reconciler.SweepAsync(ProductionThreshold, CancellationToken.None);

        terminated.Should().Be(2);
        (await _store.FindAsync(first, CancellationToken.None))!.Status.Should().Be(WorkflowStatus.Failed);
        (await _store.FindAsync(second, CancellationToken.None))!.Status.Should().Be(WorkflowStatus.Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task SweepAsync_rejects_a_non_positive_threshold(int seconds)
    {
        // At zero the store's "updated_at < threshold" predicate collapses to "updated_at < now()", which
        // matches every Running run in the system regardless of how recently it progressed — the exact failure
        // mode the falsifiability demonstration in task-7-report.md exercised manually before this guard
        // existed. A negative value is equally nonsensical (a threshold in the future). Neither expresses any
        // legitimate caller intent, so both are rejected before FindStrandedAsync is ever called.
        var act = () => _reconciler.SweepAsync(TimeSpan.FromSeconds(seconds), CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task SweepAsync_does_not_let_one_runs_failure_abandon_the_rest_of_the_batch()
    {
        // Oldest-first ordering (see OrmWorkflowStoreTests.FindStrandedAsync_orders_the_most_stranded_run_first)
        // means an unisolated throw on the first run would starve every run behind it forever — the same run
        // would re-head every future sweep's batch. throwsOnId's FailStrandedAsync always throws for one
        // specific run and delegates to the real store for every other call, simulating exactly that shape of
        // infrastructure fault without needing to actually break the database mid-sweep.
        var throwsId = await _store.StartAsync("manufacture", 1, "c-isolation-throws", "implement", initialVariables: null, CancellationToken.None);
        var survivesId = await _store.StartAsync("manufacture", 1, "c-isolation-survives", "implement", initialVariables: null, CancellationToken.None);
        await BackdateUpdatedAtAsync(throwsId, TimeSpan.FromHours(2));
        await BackdateUpdatedAtAsync(survivesId, TimeSpan.FromHours(1));

        var reconciler = new WorkflowRunReconciler(new ThrowsOnFailStrandedFor(_store, throwsId));

        var terminated = await reconciler.SweepAsync(ProductionThreshold, CancellationToken.None);

        terminated.Should().Be(1, "the throwing run must be skipped, not counted, but must not stop the survivor from being counted");
        (await _store.FindAsync(throwsId, CancellationToken.None))!.Status.Should().Be(WorkflowStatus.Running, "the throwing call left this run exactly as FindStrandedAsync found it");
        (await _store.FindAsync(survivesId, CancellationToken.None))!.Status.Should().Be(WorkflowStatus.Failed, "a later run in the same batch must still be terminated despite an earlier run's call throwing");
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

    /// <summary>Delegates every call to <paramref name="inner"/> except <see cref="FailStrandedAsync"/> for <paramref name="throwsForRunId"/>, which always throws — simulating an infrastructure fault on one run in a batch without needing to actually break the database mid-sweep.</summary>
    private sealed class ThrowsOnFailStrandedFor(IWorkflowStore inner, Guid throwsForRunId) : IWorkflowStore
    {
        public ValueTask<Guid> StartAsync(WorkflowStartRequest request, CancellationToken ct) =>
            inner.StartAsync(request, ct);

        public ValueTask<WorkflowRun?> FindAsync(Guid runId, CancellationToken ct) => inner.FindAsync(runId, ct);

        public ValueTask CompleteNodeAsync(Guid runId, long seq, WorkflowTransition transition, NodeResult result, CancellationToken ct) =>
            inner.CompleteNodeAsync(runId, seq, transition, result, ct);

        public ValueTask<Result> ResumeAsync(Guid runId, string signal, string? payload, CancellationToken ct) =>
            inner.ResumeAsync(runId, signal, payload, ct);

        public ValueTask FailAsync(Guid runId, string errorMessage, CancellationToken ct) => inner.FailAsync(runId, errorMessage, ct);

        public ValueTask<bool> FailStrandedAsync(Guid runId, long expectedSeq, string errorMessage, CancellationToken ct) =>
            runId == throwsForRunId
                ? throw new InvalidOperationException("simulated infrastructure fault for one run in the batch")
                : inner.FailStrandedAsync(runId, expectedSeq, errorMessage, ct);

        public ValueTask CancelAsync(Guid runId, string reason, CancellationToken ct) => inner.CancelAsync(runId, reason, ct);

        public ValueTask<IReadOnlyList<WorkflowRun>> FindStrandedAsync(TimeSpan olderThan, CancellationToken ct) =>
            inner.FindStrandedAsync(olderThan, ct);
    }
}
