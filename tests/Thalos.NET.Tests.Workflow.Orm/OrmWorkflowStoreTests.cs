using System.Data.Async;
using System.Data.Common;
using Npgsql;
using Thalos.Workflow;
using Thalos.Workflow.Orm;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.Orm;

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
        _store = NewStore();
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
            var store = NewStore();
            try
            {
                await store.CompleteNodeAsync(runId, 1, transition, new NodeResult("ok", Empty), CancellationToken.None);
                return (Won: true, Exception: (WorkflowConcurrencyException?)null);
            }
            catch (WorkflowConcurrencyException ex)
            {
                return (Won: false, Exception: ex);
            }
        });

        var results = await Task.WhenAll(tasks);

        results.Count(r => r.Won).Should().Be(1, "exactly one of several racing completions against the same seq should win the xmin check");
        var losers = results.Where(r => !r.Won).ToArray();
        losers.Should().HaveCount(attempts - 1);
        // A loser must be rejected by the xmin guard in UpdateRunAsync directly, not by the 23505-to-
        // WorkflowConcurrencyException mapping in InsertEventAsync: that mapping only fires downstream of a
        // guard failure once the guard itself is removed (both throw the same exception type), which would
        // otherwise make this assertion pass whether or not the guard exists at all.
        losers.Should().OnlyContain(r => r.Exception!.InnerException == null,
            "a loser must fail the xmin guard directly, not fall through to the defence-in-depth 23505 mapping");
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
        var store = await ActivateApprovalProcessAsync();

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
        var store = await ActivateApprovalProcessAsync();

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
    public async Task CompleteNodeAsync_does_not_resurrect_a_run_that_was_failed_out_of_band()
    {
        var runId = await _store.StartAsync("manufacture", 1, "c-no-resurrect", "implement", CancellationToken.None);

        // FailAsync flips status but never touches current_seq — an in-flight completion for the seq the run
        // was on when it failed would otherwise still pass CompleteNodeAsync's seq check.
        await _store.FailAsync(runId, "boom", CancellationToken.None);

        var toReview = new WorkflowTransition("review", WorkflowStatus.Running, null, WorkflowEventKind.Completed);
        var act = () => _store.CompleteNodeAsync(runId, 1, toReview, new NodeResult("ok", Empty), CancellationToken.None).AsTask();

        var thrown = await act.Should().ThrowAsync<WorkflowConcurrencyException>();
        thrown.Which.InnerException.Should().BeNull("the status guard throws directly; only the defence-in-depth 23505 mapping downstream of it carries an inner PostgresException, and removing the guard must not silently fall through to that mapping and still pass");
        var run = await _store.FindAsync(runId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Failed, "a late completion must not resurrect a run that already left the running state");
        run.CurrentNode.Should().Be("implement");
    }

    [Fact]
    public async Task FailAsync_is_idempotent_when_called_twice()
    {
        var runId = await _store.StartAsync("manufacture", 1, "c-fail-twice", "implement", CancellationToken.None);

        await _store.FailAsync(runId, "boom", CancellationToken.None);
        // A naive retry would insert a second event at the same (run_id, seq) — the terminal-state guard is
        // what turns this into a no-op instead of a unique-constraint violation.
        await _store.FailAsync(runId, "boom again", CancellationToken.None);

        var run = await _store.FindAsync(runId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Failed);
        run.LastError.Should().Be("boom", "the second call is a no-op — it must not overwrite the first failure's recorded error");
        (await CountAsync("SELECT count(*) FROM workflow_run_event WHERE run_id = @id", runId)).Should().Be(2, "Entered + the one Failed event — the retry adds nothing");
    }

    [Fact]
    public async Task CancelAsync_is_idempotent_on_an_already_failed_run()
    {
        var runId = await _store.StartAsync("manufacture", 1, "c-cancel-after-fail", "implement", CancellationToken.None);

        await _store.FailAsync(runId, "boom", CancellationToken.None);
        await _store.CancelAsync(runId, "operator abort", CancellationToken.None);

        var run = await _store.FindAsync(runId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Failed, "a run that already reached a terminal state stays there — Cancel does not override Fail");
        run.LastError.Should().Be("boom");
        (await CountAsync("SELECT count(*) FROM workflow_run_event WHERE run_id = @id", runId)).Should().Be(2, "Entered + Failed — CancelAsync on an already-terminal run adds nothing");
    }

    [Fact]
    public async Task FailAsync_is_idempotent_on_a_succeeded_run()
    {
        var runId = await _store.StartAsync("manufacture", 1, "c-fail-after-succeed", "implement", CancellationToken.None);
        var toDone = new WorkflowTransition("done", WorkflowStatus.Succeeded, null, WorkflowEventKind.Completed);
        await _store.CompleteNodeAsync(runId, 1, toDone, new NodeResult("ok", Empty), CancellationToken.None);

        // Succeeded is the one terminal-guard branch with a plausible "this call was a mistake" reading — a
        // late or misdirected FailAsync must not downgrade a run that already succeeded. Pinned in a test
        // rather than left to the guard's comment alone.
        await _store.FailAsync(runId, "boom", CancellationToken.None);

        var run = await _store.FindAsync(runId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Succeeded, "a run that already succeeded must not be overwritten to Failed");
        run.LastError.Should().BeNull();
        (await CountAsync("SELECT count(*) FROM workflow_run_event WHERE run_id = @id", runId)).Should().Be(2, "Entered + the Completed-to-Succeeded event — FailAsync on a Succeeded run adds nothing");
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

    // --- FailStrandedAsync -----------------------------------------------------------------------------------

    [Fact]
    public async Task FailStrandedAsync_fails_the_run_when_the_seq_still_matches()
    {
        var runId = await _store.StartAsync("manufacture", 1, "c-stranded-fail", "implement", CancellationToken.None);

        var failed = await _store.FailStrandedAsync(runId, expectedSeq: 1, "stranded", CancellationToken.None);

        failed.Should().BeTrue();
        var run = await _store.FindAsync(runId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Failed);
        run.LastError.Should().Be("stranded");
    }

    [Fact]
    public async Task FailStrandedAsync_no_ops_instead_of_destroying_a_gate_a_concurrent_dispatch_just_parked()
    {
        // This is the regression FailStrandedAsync exists to close: a run the sweep read at seq 1 (Running)
        // that a concurrent dispatcher completes into Awaiting before the sweep's own FailAsync-equivalent
        // write lands. CompleteNodeAsync bumps current_seq to 2 on that transition — the sweep's stale
        // expectedSeq: 1 must therefore no-op, not fail the gate it never should have touched.
        var runId = await _store.StartAsync("approval", 1, "c-stranded-race", "start", CancellationToken.None);
        var toGate = new WorkflowTransition("gate", WorkflowStatus.Awaiting, "ok", WorkflowEventKind.Awaiting);
        await _store.CompleteNodeAsync(runId, seq: 1, toGate, new NodeResult(null, Empty), CancellationToken.None);

        var failed = await _store.FailStrandedAsync(runId, expectedSeq: 1, "stranded", CancellationToken.None);

        failed.Should().BeFalse("the run moved on to a different seq — this call must not overwrite whatever it became");
        var run = await _store.FindAsync(runId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Awaiting, "the gate the run parked at must survive completely untouched");
        run.AwaitingSignal.Should().Be("ok");
        run.LastError.Should().BeNull();
    }

    [Fact]
    public async Task FailStrandedAsync_no_ops_on_an_already_terminal_run()
    {
        var runId = await _store.StartAsync("manufacture", 1, "c-stranded-terminal", "implement", CancellationToken.None);
        await _store.FailAsync(runId, "boom", CancellationToken.None);

        // FailAsync never advances current_seq (see its own remarks), so the stranded sweep's snapshot would
        // still show seq 1 here — the terminal-state guard, not the seq guard, is what must catch this case.
        var failed = await _store.FailStrandedAsync(runId, expectedSeq: 1, "stranded", CancellationToken.None);

        failed.Should().BeFalse();
        var run = await _store.FindAsync(runId, CancellationToken.None);
        run!.LastError.Should().Be("boom", "an already-terminal run's recorded error must not be overwritten");
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

    [Fact]
    public async Task FindStrandedAsync_excludes_a_stale_awaiting_run()
    {
        // A run parked Awaiting has nothing in flight by design and may legitimately sit there for days — it
        // must never be reported stranded no matter how stale, or a consumer sweeping this list would destroy
        // exactly the human-in-the-loop work the gate exists to protect. See WorkflowRunReconciler's remarks.
        var runId = await _store.StartAsync("approval", 1, "c-stale-awaiting", "start", CancellationToken.None);
        var toGate = new WorkflowTransition("gate", WorkflowStatus.Awaiting, "ok", WorkflowEventKind.Awaiting);
        await _store.CompleteNodeAsync(runId, seq: 1, toGate, new NodeResult(null, Empty), CancellationToken.None);
        await ExecuteAsync("UPDATE workflow_run SET updated_at = now() - interval '10 days' WHERE id = @id", runId);

        var stranded = await _store.FindStrandedAsync(TimeSpan.FromMinutes(10), CancellationToken.None);

        stranded.Select(r => r.Id).Should().NotContain(runId);
    }

    [Fact]
    public async Task FindStrandedAsync_orders_the_most_stranded_run_first()
    {
        // Oldest-updated-first is what makes a backlog past the cap drain over successive sweeps true, rather
        // than the same handful of runs winning the cap on every call while the rest starve forever. Nothing
        // else in this suite pins the order, so flipping ORDER BY to DESC would pass every other test here and
        // still be wrong — that is the regression this test is falsifiable against. Dropping ORDER BY entirely
        // is not: these three rows are inserted, and later updated, oldest-to-newest, so an unordered scan of
        // this table happens to come back in the same sequence and would still pass.
        var oldestId = await _store.StartAsync("manufacture", 1, "c-order-oldest", "implement", CancellationToken.None);
        var middleId = await _store.StartAsync("manufacture", 1, "c-order-middle", "implement", CancellationToken.None);
        var newestId = await _store.StartAsync("manufacture", 1, "c-order-newest", "implement", CancellationToken.None);

        await ExecuteAsync("UPDATE workflow_run SET updated_at = now() - interval '3 hours' WHERE id = @id", oldestId);
        await ExecuteAsync("UPDATE workflow_run SET updated_at = now() - interval '2 hours' WHERE id = @id", middleId);
        await ExecuteAsync("UPDATE workflow_run SET updated_at = now() - interval '1 hour' WHERE id = @id", newestId);

        var stranded = await _store.FindStrandedAsync(TimeSpan.FromMinutes(10), CancellationToken.None);

        stranded.Select(r => r.Id).Should().ContainInOrder(oldestId, middleId, newestId);
    }

    // --- helpers -----------------------------------------------------------------------------------------

    private OrmWorkflowStore StoreWithFailingOutbox() => new(
        new WorkflowOrmOptions
        {
            ConnectionString = pg.ConnectionString,
            OutboxStoreFactory = connection => new ThrowingOutboxStore(connection),
        },
        NewDefinitionStore());

    /// <summary>
    /// A store wired to the real <see cref="OrmProcessDefinitionStore"/> over the same database — the same pairing
    /// <c>AddWorkflowOrm</c> composes. Nothing here registers process definitions in memory: a test that needs a
    /// definition resolvable puts it in the table through <see cref="ActivateApprovalProcessAsync"/>, which is now
    /// the only way a definition becomes runnable.
    /// </summary>
    private OrmWorkflowStore NewStore() =>
        new(new WorkflowOrmOptions { ConnectionString = pg.ConnectionString }, NewDefinitionStore());

    private OrmProcessDefinitionStore NewDefinitionStore() =>
        new(new WorkflowOrmOptions { ConnectionString = pg.ConnectionString });

    /// <summary>The YAML behind <see cref="ActivateApprovalProcessAsync"/>: a task node, an approval gate, a terminal.</summary>
    private const string ApprovalYaml = """
        process: approval
        version: 1
        nodes:
          start: { next: gate }
          gate: { await: ok, next: done }
          done: { terminal: succeeded }
        """;

    /// <summary>
    /// Writes the approval process into <c>process_definition</c> and returns a workflow store that resolves
    /// against it. This replaces the in-memory registry these two resume tests used to seed: a definition now has
    /// to be in the table for a resume to find it, which is the whole point of the change they were updated for.
    /// </summary>
    private async Task<OrmWorkflowStore> ActivateApprovalProcessAsync()
    {
        var definitions = NewDefinitionStore();
        var definition = ProcessLoader.Load(ApprovalYaml);
        definition.IsSuccess.Should().BeTrue(definition.IsFailure ? definition.Error : "");
        await definitions.UpsertAndActivateAsync(definition.Value, ApprovalYaml, CancellationToken.None);

        return new OrmWorkflowStore(new WorkflowOrmOptions { ConnectionString = pg.ConnectionString }, definitions);
    }

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

    /// <summary>
    /// Delegates to a real <see cref="OrmOutboxStore"/> — the row genuinely gets written, on the caller's
    /// transaction — and only then throws. A store that enqueued outside the transaction, or that never wrote
    /// the row at all, would leave <c>outboxmessages</c> empty either way, so
    /// <c>(await CountAsync("SELECT count(*) FROM outboxmessages", null)).Should().Be(0)</c> after the rollback
    /// is only capable of catching a real atomicity bug because a row was actually inserted first. A store
    /// that throws before writing anything (the shape this replaced) makes that assertion true by construction,
    /// whether or not the real enqueue is transactional.
    /// </summary>
    private sealed class ThrowingOutboxStore(IAsyncDbConnection connection) : IOutboxStore
    {
        private readonly OrmOutboxStore _inner = new(connection);

        public async ValueTask EnqueueAsync(string typeName, ReadOnlyMemory<byte> payload, DbTransaction? transaction, CancellationToken ct)
        {
            await _inner.EnqueueAsync(typeName, payload, transaction, ct);
            throw new InvalidOperationException("simulated outbox enqueue failure after the row was written");
        }

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
