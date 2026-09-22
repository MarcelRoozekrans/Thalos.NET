using System.Data.Common;
using Npgsql;
using Thalos.Workflow;
using Thalos.Workflow.Orm;
using ZeroAlloc.Outbox;

namespace Thalos.Tests.Workflow.Orm;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Docker")]
public sealed class OrmWorkflowStoreTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly IReadOnlyDictionary<string, object?> Empty = new Dictionary<string, object?>(StringComparer.Ordinal);

    private OrmWorkflowStore _store = null!;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        _store = new OrmWorkflowStore(new WorkflowOrmOptions { ConnectionString = pg.ConnectionString });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // --- Step 2 / Step 3: the single-transaction property -------------------------------------------------

    [Fact]
    public async Task CompleteNode_commits_event_run_update_and_enqueue_atomically()
    {
        var runId = await _store.StartAsync("manufacture", version: 1, correlationKey: "c1", "implement", CancellationToken.None);

        await _store.CompleteNodeAsync(
            runId, seq: 1,
            new WorkflowTransition("review", WorkflowStatus.Running, null, WorkflowEventKind.Completed),
            new NodeResult("ok", Empty), CancellationToken.None);

        (await ScalarAsync<string>("SELECT current_node FROM workflow_run WHERE id = @id", runId)).Should().Be("review");
        (await CountAsync("SELECT count(*) FROM workflow_run_event WHERE run_id = @id", runId)).Should().Be(2);
        (await CountAsync("SELECT count(*) FROM outboxmessages", null)).Should().Be(1);
    }

    [Fact]
    public async Task CompleteNode_rolls_back_all_four_writes_when_the_enqueue_throws()
    {
        var runId = await _store.StartAsync("manufacture", 1, "c2", "implement", CancellationToken.None);
        var store = StoreWithFailingOutbox();
        var toReview = new WorkflowTransition("review", WorkflowStatus.Running, null, WorkflowEventKind.Completed);

        var act = () => store.CompleteNodeAsync(runId, 1, toReview, new NodeResult("ok", Empty), CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<Exception>();
        (await ScalarAsync<string>("SELECT current_node FROM workflow_run WHERE id = @id", runId)).Should().Be("implement");
        (await CountAsync("SELECT count(*) FROM workflow_run_event WHERE run_id = @id", runId)).Should().Be(1);
        (await CountAsync("SELECT count(*) FROM outboxmessages", null)).Should().Be(0);
    }

    // --- Step 4: optimistic concurrency on xmin ------------------------------------------------------------

    [Fact]
    public async Task CompleteNodeAsync_exactly_one_of_several_concurrent_completions_wins()
    {
        var runId = await _store.StartAsync("manufacture", 1, "c-race", "implement", CancellationToken.None);
        var transition = new WorkflowTransition("review", WorkflowStatus.Running, null, WorkflowEventKind.Completed);

        const int attempts = 8;
        var tasks = Enumerable.Range(0, attempts).Select(async _ =>
        {
            var store = new OrmWorkflowStore(new WorkflowOrmOptions { ConnectionString = pg.ConnectionString });
            try
            {
                await store.CompleteNodeAsync(runId, 1, transition, new NodeResult("ok", Empty), CancellationToken.None);
                return true;
            }
            catch (WorkflowConcurrencyException)
            {
                return false;
            }
        });

        var results = await Task.WhenAll(tasks);

        results.Count(won => won).Should().Be(1, "exactly one of several racing completions against the same seq should win the xmin check");
        results.Count(won => !won).Should().Be(attempts - 1);
        (await ScalarAsync<long>("SELECT current_seq FROM workflow_run WHERE id = @id", runId)).Should().Be(2);
    }

    // --- StartAsync / FindAsync ----------------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_seeds_visits_with_the_start_node_already_counted_as_one_entry()
    {
        var runId = await _store.StartAsync("manufacture", 1, "c3", "implement", CancellationToken.None);

        var run = await _store.FindAsync(runId, CancellationToken.None);

        run.Should().NotBeNull();
        run!.CurrentNode.Should().Be("implement");
        run.CurrentSeq.Should().Be(1);
        run.Status.Should().Be(WorkflowStatus.Running);
        run.Visits.Should().ContainKey("implement").WhoseValue.Should().Be(1);
    }

    [Fact]
    public async Task CompleteNodeAsync_merges_a_new_nodes_variables_without_dropping_earlier_ones()
    {
        var runId = await _store.StartAsync("manufacture", 1, "c-vars-merge", "implement", CancellationToken.None);

        await _store.CompleteNodeAsync(
            runId, seq: 1,
            new WorkflowTransition("review", WorkflowStatus.Running, null, WorkflowEventKind.Completed),
            new NodeResult("ok", new Dictionary<string, object?>(StringComparer.Ordinal) { ["plan"] = "fast-track", ["owner"] = "alice" }),
            CancellationToken.None);

        // The second node's own payload never mentions "owner" and overwrites "plan" — a replace-semantics bug
        // would drop "owner" here and this assertion would fail; a merge keeps it.
        await _store.CompleteNodeAsync(
            runId, seq: 2,
            new WorkflowTransition("publish", WorkflowStatus.Running, null, WorkflowEventKind.Completed),
            new NodeResult("done", new Dictionary<string, object?>(StringComparer.Ordinal) { ["plan"] = "override" }),
            CancellationToken.None);

        var run = await _store.FindAsync(runId, CancellationToken.None);

        run!.Variables.Should().ContainKey("plan").WhoseValue.Should().Be("override", "a later node's write to the same key wins");
        run.Variables.Should().ContainKey("owner").WhoseValue.Should().Be("alice", "a later node that never mentions an earlier key must not drop it");
    }

    [Fact]
    public async Task CompleteNodeAsync_leaves_the_variable_bag_intact_when_a_node_returns_no_variables()
    {
        var runId = await _store.StartAsync("manufacture", 1, "c-vars-empty", "implement", CancellationToken.None);

        await _store.CompleteNodeAsync(
            runId, seq: 1,
            new WorkflowTransition("review", WorkflowStatus.Running, null, WorkflowEventKind.Completed),
            new NodeResult("ok", new Dictionary<string, object?>(StringComparer.Ordinal) { ["plan"] = "fast-track" }),
            CancellationToken.None);

        // The second node produces no variables at all — a replace-semantics bug would wipe the bag to empty.
        await _store.CompleteNodeAsync(
            runId, seq: 2,
            new WorkflowTransition("publish", WorkflowStatus.Running, null, WorkflowEventKind.Completed),
            new NodeResult("done", Empty),
            CancellationToken.None);

        var run = await _store.FindAsync(runId, CancellationToken.None);

        run!.Variables.Should().ContainKey("plan").WhoseValue.Should().Be("fast-track", "a node returning no variables must not clear what an earlier node wrote");
    }

    [Fact]
    public async Task StartAsync_seeds_an_empty_variable_bag()
    {
        var runId = await _store.StartAsync("manufacture", 1, "c-vars-start", "implement", CancellationToken.None);

        var run = await _store.FindAsync(runId, CancellationToken.None);

        run!.Variables.Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_is_idempotent_for_the_same_correlation_key()
    {
        var first = await _store.StartAsync("manufacture", 1, "c-dup", "implement", CancellationToken.None);
        var second = await _store.StartAsync("manufacture", 1, "c-dup", "implement", CancellationToken.None);

        second.Should().Be(first);
        (await CountAsync("SELECT count(*) FROM workflow_run WHERE correlation_key = @id", "c-dup")).Should().Be(1);
    }

    [Fact]
    public async Task CompleteNodeAsync_does_not_increment_visits_when_the_transition_parks_on_the_same_node()
    {
        var runId = await _store.StartAsync("gated", 1, "c-gate", "gate", CancellationToken.None);

        // A gate parking on itself: WorkflowTransition.NextNode == the run's current node.
        await _store.CompleteNodeAsync(
            runId, seq: 1,
            new WorkflowTransition("gate", WorkflowStatus.Awaiting, "approve", WorkflowEventKind.Awaiting),
            new NodeResult(null, Empty), CancellationToken.None);

        var run = await _store.FindAsync(runId, CancellationToken.None);

        run!.Visits["gate"].Should().Be(1, "a self-transition is not a new entry and must not double-count the visit");
        run.Status.Should().Be(WorkflowStatus.Awaiting);
        run.AwaitingSignal.Should().Be("approve");
    }

    // --- ResumeAsync: the store never computes the gate's successor itself --------------------------------

    [Fact]
    public async Task ResumeAsync_defers_to_the_interpreter_for_the_gates_successor()
    {
        var process = ApprovalProcess();
        var store = new OrmWorkflowStore(new WorkflowOrmOptions
        {
            ConnectionString = pg.ConnectionString,
        }.AddProcess(process));

        var runId = await store.StartAsync("approval", 1, "c-resume", "start", CancellationToken.None);
        await store.CompleteNodeAsync(
            runId, seq: 1,
            new WorkflowTransition("gate", WorkflowStatus.Awaiting, "ok", WorkflowEventKind.Awaiting),
            new NodeResult(null, Empty), CancellationToken.None);

        var result = await store.ResumeAsync(runId, "ok", "approved", CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
        var run = await store.FindAsync(runId, CancellationToken.None);
        run!.CurrentNode.Should().Be("done");
        run.Status.Should().Be(WorkflowStatus.Running);
        run.AwaitingSignal.Should().BeNull();
        run.Variables.Should().ContainKey("payload").WhoseValue.Should().Be("approved", "ResumeAsync's payload merges into the run's variable bag the same way a node's own variables do");
        (await ScalarAsync<string>("SELECT kind FROM workflow_run_event WHERE run_id = @id ORDER BY seq DESC LIMIT 1", runId)).Should().Be(nameof(WorkflowEventKind.Resumed));
    }

    [Fact]
    public async Task ResumeAsync_fails_when_the_signal_does_not_match()
    {
        var process = ApprovalProcess();
        var store = new OrmWorkflowStore(new WorkflowOrmOptions
        {
            ConnectionString = pg.ConnectionString,
        }.AddProcess(process));

        var runId = await store.StartAsync("approval", 1, "c-resume-2", "start", CancellationToken.None);
        await store.CompleteNodeAsync(
            runId, seq: 1,
            new WorkflowTransition("gate", WorkflowStatus.Awaiting, "ok", WorkflowEventKind.Awaiting),
            new NodeResult(null, Empty), CancellationToken.None);

        var result = await store.ResumeAsync(runId, "wrong-signal", null, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    // --- FailAsync / CancelAsync ----------------------------------------------------------------------------

    [Fact]
    public async Task FailAsync_marks_the_run_failed_and_records_the_error()
    {
        var runId = await _store.StartAsync("manufacture", 1, "c-fail", "implement", CancellationToken.None);

        await _store.FailAsync(runId, "boom", CancellationToken.None);

        var run = await _store.FindAsync(runId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Failed);
        run.LastError.Should().Be("boom");
        (await CountAsync("SELECT count(*) FROM workflow_run_event WHERE run_id = @id", runId)).Should().Be(2);
    }

    [Fact]
    public async Task CancelAsync_marks_the_run_cancelled_and_records_the_reason()
    {
        var runId = await _store.StartAsync("manufacture", 1, "c-cancel", "implement", CancellationToken.None);

        await _store.CancelAsync(runId, "operator abort", CancellationToken.None);

        var run = await _store.FindAsync(runId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Cancelled);
        run.LastError.Should().Be("operator abort");
    }

    // --- FindStrandedAsync -----------------------------------------------------------------------------------

    [Fact]
    public async Task FindStrandedAsync_returns_only_runs_older_than_the_threshold()
    {
        var staleId = await _store.StartAsync("manufacture", 1, "c-stale", "implement", CancellationToken.None);
        var freshId = await _store.StartAsync("manufacture", 1, "c-fresh", "implement", CancellationToken.None);

        await ExecuteAsync("UPDATE workflow_run SET updated_at = now() - interval '1 hour' WHERE id = @id", staleId);

        var stranded = await _store.FindStrandedAsync(TimeSpan.FromMinutes(10), CancellationToken.None);

        stranded.Select(r => r.Id).Should().Contain(staleId);
        stranded.Select(r => r.Id).Should().NotContain(freshId);
    }

    // --- helpers -----------------------------------------------------------------------------------------

    private OrmWorkflowStore StoreWithFailingOutbox() => new(new WorkflowOrmOptions
    {
        ConnectionString = pg.ConnectionString,
        OutboxStoreFactory = _ => new ThrowingOutboxStore(),
    });

    private static ProcessDefinition ApprovalProcess() => new()
    {
        Name = "approval",
        Version = 1,
        StartNode = "start",
        Nodes = new Dictionary<string, ProcessNode>(StringComparer.Ordinal)
        {
            ["start"] = new ProcessNode { Next = "gate" },
            ["gate"] = new ProcessNode { Await = "ok", Next = "done" },
            ["done"] = new ProcessNode { Terminal = "succeeded" },
        },
    };

    private async Task<T?> ScalarAsync<T>(string sql, object? parameter)
    {
        await using var connection = new NpgsqlConnection(pg.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        if (parameter is not null)
        {
            cmd.Parameters.AddWithValue("id", parameter);
        }

        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    private async Task<long> CountAsync(string sql, object? parameter)
    {
        var count = await ScalarAsync<long>(sql, parameter);
        return count;
    }

    private async Task ExecuteAsync(string sql, object parameter)
    {
        await using var connection = new NpgsqlConnection(pg.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("id", parameter);
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class ThrowingOutboxStore : IOutboxStore
    {
        public ValueTask EnqueueAsync(string typeName, ReadOnlyMemory<byte> payload, DbTransaction? transaction, CancellationToken ct) =>
            throw new InvalidOperationException("simulated outbox enqueue failure");

        public ValueTask EnqueueDeferredAsync(string typeName, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<OutboxEntry>> FetchPendingAsync(int batchSize, CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask MarkSucceededAsync(OutboxMessageId id, CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask MarkFailedAsync(OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask DeadLetterAsync(OutboxMessageId id, string error, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
