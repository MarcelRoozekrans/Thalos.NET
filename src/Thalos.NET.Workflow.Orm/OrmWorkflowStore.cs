using System.Data.Async.Adapters;
using System.Text.Json;
using Npgsql;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.Orm;
using ZeroAlloc.Results;

namespace Thalos.Workflow.Orm;

/// <summary>
/// PostgreSQL-backed <see cref="IWorkflowStore"/>: raw ADO.NET (no EF Core) over <c>workflow_run</c> and the
/// append-only <c>workflow_run_event</c>, with each dispatch message enqueued through
/// <c>ZeroAlloc.Outbox.Orm.OrmOutboxStore</c> in the same <see cref="System.Data.Common.DbTransaction"/> as the
/// event append and the run write — one commit or none, so a run never records a position without the work
/// behind that node also scheduled, and never schedules that work without the write having landed. That holds
/// from the very first node: <see cref="StartAsync"/> enqueues its start node's dispatch on the transaction that
/// inserts the run, so there is no state in which a started run exists with an empty outbox.
/// </summary>
/// <remarks>
/// Concurrency is optimistic on PostgreSQL's built-in <c>xmin</c> system column, not a hand-rolled version
/// column: the run row is read (capturing its current <c>xmin</c>) at the start of the mutating operation, and
/// the closing <c>UPDATE</c> carries <c>WHERE id = @id AND xmin::text::bigint = @expectedXmin</c>. A concurrent
/// writer that commits first changes the row's <c>xmin</c>, so the loser's <c>UPDATE</c> affects zero rows —
/// <see cref="WorkflowConcurrencyException"/> is thrown rather than the loss passing silently. <see cref="CompleteNodeAsync"/>
/// additionally checks the caller-supplied <c>seq</c> against the run's persisted <see cref="WorkflowRun.CurrentSeq"/>
/// before touching anything, so a stale or redelivered completion is rejected the same way.
/// </remarks>
public sealed class OrmWorkflowStore(WorkflowOrmOptions options, IProcessDefinitionStore definitions) : IWorkflowStore
{
    private const string SelectRunSql = """
        SELECT id, process, process_version, correlation_key, current_node, current_seq, status, awaiting_signal, visits, variables, last_error, xmin::text::bigint AS xmin
        FROM workflow_run
        """;

    /// <summary>
    /// <see cref="StartAsync"/>'s <c>current_seq</c> for a brand-new run: it is on its first node's execution.
    /// Tied to <see cref="SeedEventSeq"/> by a shared symbol, not left as two numeric literals in different
    /// methods, because a future edit to one without the other would silently reintroduce the tie this pair
    /// exists to avoid — the seeded "Entered" event and the run's own <c>current_seq</c> deliberately differ by
    /// one specifically so the first <see cref="CompleteNodeAsync"/> call's own event never collides with it.
    /// </summary>
    private const long InitialCurrentSeq = 1;

    /// <summary>The seq <see cref="StartAsync"/> records the seeded "Entered" event at — see <see cref="InitialCurrentSeq"/>.</summary>
    private const long SeedEventSeq = InitialCurrentSeq - 1;

    /// <summary>The bag a run started without initial variables gets — empty, never SQL NULL. See <see cref="StartAsync"/>.</summary>
    private static readonly IReadOnlyDictionary<string, object?> EmptyVariables =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    private readonly WorkflowOrmOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Where <see cref="ResumeAsync"/> gets the run's <see cref="ProcessDefinition"/> from — the same
    /// <see cref="IProcessDefinitionStore"/> <c>ProcessDefinitionSync</c> writes to and
    /// <c>WorkflowNodeDispatcher</c> reads from, so a gate resumes against exactly the definition a dispatch
    /// would have run it against. This store holds no process registry of its own; there is nowhere for a second
    /// answer to live.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolution takes the run's pinned <c>(Process, ProcessVersion)</c>, never the currently active version: a
    /// run parked at a gate for days must come back to the graph it started on even if newer versions activated
    /// meanwhile.
    /// </para>
    /// <para>
    /// <b>What this read costs.</b> <see cref="ResumeAsync"/> calls it while its own transaction is open, so it
    /// takes a <em>second</em> connection from the same pool. That is safe for locking — a different table, and
    /// no lock the run row's writer waits on — but it should not be waved away as free just because a warm cache
    /// makes it no I/O at all. The case that decides whether this is sound is the loaded one: a cold host
    /// resuming many parked gates at once would otherwise have every resume holding one connection and queuing
    /// for another, which past enough concurrency stops being slow and starts timing out.
    /// <c>CachingProcessDefinitionStore</c>'s single-flight is what bounds it, capping concurrent
    /// second-connection demand at one per distinct version however many resumes arrive together. A store
    /// constructed here with an un-decorated <see cref="IProcessDefinitionStore"/> gives that guarantee up;
    /// <c>AddWorkflowOrm</c> always wires the decorator.
    /// </para>
    /// </remarks>
    private readonly IProcessDefinitionStore _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));

    /// <inheritdoc/>
    public async ValueTask<Guid> StartAsync(
        string process,
        int version,
        string correlationKey,
        string startNode,
        IReadOnlyDictionary<string, object?>? initialVariables,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(process);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(startNode);
        WorkflowVariableBlock.ThrowIfOverKeyLimit(initialVariables, nameof(initialVariables));

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var id = Guid.NewGuid();
        // Null and an empty bag are the same thing to the column: both store '{}', never SQL NULL, so a run's
        // variables read back as an empty dictionary rather than something a consumer has to null-check.
        var seeded = initialVariables ?? EmptyVariables;
        var inserted = await InsertRunAsync(connection, tx, id, process, version, correlationKey, startNode, seeded, ct).ConfigureAwait(false);

        if (inserted == 0)
        {
            // Idempotent lookup: a run for this correlation key already exists. Return its id rather than
            // create a duplicate — no event is appended, because no new run was entered, and no dispatch is
            // enqueued either: the run this returns is at whatever seq its own progress has reached, so a
            // message minted here would be a second delivery for a node something else already scheduled.
            await using var select = connection.CreateCommand();
            select.Transaction = tx;
            select.CommandText = "SELECT id FROM workflow_run WHERE correlation_key = @correlationKey";
            select.Parameters.AddWithValue("correlationKey", correlationKey);
            var existing = (Guid)(await select.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return existing;
        }

        await InsertEventAsync(
            connection, tx, id, seq: SeedEventSeq,
            fromNode: null, toNode: startNode,
            status: WorkflowStatus.Running, awaitingSignal: null,
            outcome: null, variables: seeded.Count == 0 ? null : seeded, error: null,
            kind: nameof(WorkflowEventKind.Entered), ct).ConfigureAwait(false);

        // The start node's own dispatch, on this same transaction. Without it a run created through this API
        // reaches Running with nothing in the outbox and nothing that will ever dispatch its start node — it
        // would simply sit there until a stranded-run sweep terminated it. Enqueuing here rather than leaving
        // it to a caller means the run row, the seeded event and the first dispatch all commit or all roll
        // back: there is no window in which a run exists without the work behind its first node scheduled, and
        // none in which that work is scheduled against a run that was never written.
        await EnqueueDispatchAsync(connection, tx, id, InitialCurrentSeq, startNode, ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>
    /// <see cref="StartAsync"/>'s <c>INSERT</c>, split out only to keep that method inside the analyzer's length
    /// limit. <paramref name="initialVariables"/> is written into the <c>variables</c> column as it stands, so a
    /// run starts holding exactly what it was seeded with — and, on the zero-row path below, nothing is written
    /// at all, leaving the run that already owns the key with its own bag untouched.
    /// Returns the number of rows inserted: one for a genuinely new run, zero when
    /// <c>ON CONFLICT (correlation_key) DO NOTHING</c> found the key already taken — which is how
    /// <see cref="StartAsync"/> tells the two paths apart without a separate probing read.
    /// </summary>
    private static async Task<int> InsertRunAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid id,
        string process, int version, string correlationKey, string startNode,
        IReadOnlyDictionary<string, object?> initialVariables, CancellationToken ct)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO workflow_run (id, process, process_version, correlation_key, current_node, current_seq, status, awaiting_signal, visits, variables, last_error)
            VALUES (@id, @process, @version, @correlationKey, @currentNode, @currentSeq, @status, NULL, @visits::jsonb, @variables::jsonb, NULL)
            ON CONFLICT (correlation_key) DO NOTHING
            """;
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("process", process);
        insert.Parameters.AddWithValue("version", version);
        insert.Parameters.AddWithValue("correlationKey", correlationKey);
        insert.Parameters.AddWithValue("currentNode", startNode);
        insert.Parameters.AddWithValue("currentSeq", InitialCurrentSeq);
        insert.Parameters.AddWithValue("status", nameof(WorkflowStatus.Running));
        insert.Parameters.AddWithValue("visits", JsonSerializer.Serialize(new Dictionary<string, int>(StringComparer.Ordinal) { [startNode] = 1 }));
        insert.Parameters.AddWithValue("variables", JsonSerializer.Serialize(initialVariables));

        return await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<WorkflowRun?> FindAsync(Guid runId, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var row = await ReadRunRowAsync(connection, transaction: null, runId, ct).ConfigureAwait(false);
        return row is null ? null : ToWorkflowRun(row);
    }

    /// <inheritdoc/>
    public async ValueTask CompleteNodeAsync(Guid runId, long seq, WorkflowTransition transition, NodeResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(result);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var row = await ReadRunRowAsync(connection, tx, runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Workflow run '{runId}' was not found.");

        // A run that was cancelled or failed out-of-band has no CurrentSeq change to reflect that — FailAsync
        // and CancelAsync only flip status — so a completion racing in after either would otherwise pass the
        // seq check below, read a still-matching xmin, and resurrect the run to Running. Guarding on status
        // here is what actually stops that: the exact race cancellation exists to prevent.
        if (row.Status is not WorkflowStatus.Running)
        {
            throw new WorkflowConcurrencyException(
                $"Workflow run '{runId}' is {row.Status}, not Running — a completion for seq {seq} arrived after the run left the running state.");
        }

        if (row.CurrentSeq != seq)
        {
            throw new WorkflowConcurrencyException(
                $"Workflow run '{runId}' expected seq {row.CurrentSeq} but completion reported seq {seq} — stale or redelivered completion.");
        }

        await ApplyTransitionAsync(connection, tx, runId, seq, row, transition, result.Outcome, result.Variables, ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<Result> ResumeAsync(Guid runId, string signal, string? payload, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signal);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var row = await ReadRunRowAsync(connection, tx, runId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure($"Workflow run '{runId}' was not found.");
        }

        if (row.Status != WorkflowStatus.Awaiting || !string.Equals(row.AwaitingSignal, signal, StringComparison.Ordinal))
        {
            return Result.Failure($"Workflow run '{runId}' is not awaiting signal '{signal}'.");
        }

        // Resolved on the run's pinned (Process, ProcessVersion), not on whatever version is currently active —
        // see _definitions for why, and for what this read costs while the transaction is open. The definition
        // store's own error message already names the process and version, so it is surfaced verbatim.
        var definition = await _definitions.GetAsync(row.Process, row.ProcessVersion, ct).ConfigureAwait(false);
        if (definition.IsFailure)
        {
            return Result.Failure(definition.Error);
        }

        var process = definition.Value;

        // Advance is called with Status still Awaiting (per IWorkflowStore.ResumeAsync's contract) — that is
        // the only signal Advance has to tell this resume from a fresh arrival at the same gate. The store
        // never computes the gate's successor itself.
        var run = ToWorkflowRun(row);
        var variables = payload is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal) { ["payload"] = payload };

        var transitionResult = WorkflowInterpreter.Advance(process, run, new NodeResult(null, variables));
        if (transitionResult.IsFailure)
        {
            return Result.Failure(transitionResult.Error);
        }

        // A concurrency loss here surfaces as Result.Failure, not a thrown WorkflowConcurrencyException, so a
        // caller matching on this method's Result return does not need a second, exception-based error channel
        // to also handle — every failure mode ResumeAsync can hit arrives the same way.
        try
        {
            await ApplyTransitionAsync(connection, tx, runId, row.CurrentSeq, row, transitionResult.Value, outcome: null, variables, ct).ConfigureAwait(false);
        }
        catch (WorkflowConcurrencyException ex)
        {
            return Result.Failure(ex.Message);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    /// <inheritdoc/>
    public async ValueTask FailAsync(Guid runId, string errorMessage, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var row = await ReadRunRowAsync(connection, tx, runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Workflow run '{runId}' was not found.");

        if (IsTerminal(row.Status))
        {
            // Idempotent: the run already reached a terminal state, whether this is the same failure being
            // retried or a different terminal path won the race. Re-recording it would collide with
            // workflow_run_event's (run_id, seq) unique index — FailAsync never advances current_seq, so a
            // retry reads the identical row.CurrentSeq the first call already used — and would mean nothing
            // anyway: the run's outcome is already on record.
            return;
        }

        // The xmin-checked UPDATE runs before the event INSERT, same as ApplyTransitionAsync and for the same
        // reason: two concurrent FailAsync calls on the same Running run both clear the terminal-state guard
        // above and would otherwise both attempt to insert at the same row.CurrentSeq. With the UPDATE first,
        // the loser throws WorkflowConcurrencyException here and never reaches the INSERT at all.
        if (!await TryApplyFailedStatusAsync(connection, tx, runId, row, errorMessage, ct).ConfigureAwait(false))
        {
            throw new WorkflowConcurrencyException($"Workflow run '{runId}' was concurrently modified.");
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Unlike <see cref="FailAsync"/>, every guard here resolves to a no-op return, never a throw: a run this
    /// method declines to touch — not found, already moved past <paramref name="expectedSeq"/>, or already
    /// terminal — is not a failure of this call, it is this call correctly refusing to clobber state a
    /// concurrent write already changed. The seq check is what actually protects a gate: <c>ApplyTransitionAsync</c>
    /// bumps <c>current_seq</c> on every transition it applies, parking at a gate included, so a run that left
    /// <paramref name="expectedSeq"/> between <see cref="FindStrandedAsync"/>'s snapshot and this call's own read
    /// is a run this sweep must leave alone — whatever it became is somebody else's write to make, not this
    /// one's to overwrite.
    /// </remarks>
    public async ValueTask<bool> FailStrandedAsync(Guid runId, long expectedSeq, string errorMessage, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var row = await ReadRunRowAsync(connection, tx, runId, ct).ConfigureAwait(false);

        // Not found, moved past expectedSeq (most importantly: completed into Awaiting at a gate between
        // FindStrandedAsync's snapshot and this call's own read), or already terminal — every one of these is
        // this sweep correctly declining to touch state a concurrent write already changed, never a throw.
        if (row is null || row.CurrentSeq != expectedSeq || IsTerminal(row.Status))
        {
            return false;
        }

        // Same UPDATE-before-INSERT ordering as FailAsync, for the same defence-in-depth reason — but here a
        // losing xmin check is just one more "moved on" case, not a broken invariant, so it also resolves to a
        // no-op rather than a throw.
        return await TryApplyFailedStatusAsync(connection, tx, runId, row, errorMessage, ct).ConfigureAwait(false)
            && await CommitAndReturnTrueAsync(tx, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The xmin-checked <c>UPDATE ... SET status = 'Failed'</c> plus its paired event <c>INSERT</c>, shared by
    /// <see cref="FailAsync"/> and <see cref="FailStrandedAsync"/> — both fail <paramref name="row"/> the same
    /// way; only what a lost xmin race means to the caller differs (a throw versus a no-op), which is why this
    /// returns a bool rather than deciding that itself. Does not commit <paramref name="tx"/> — the caller does,
    /// once it has decided what a <see langword="false"/> result means for it.
    /// </summary>
    private static async Task<bool> TryApplyFailedStatusAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid runId, RunRow row, string errorMessage, CancellationToken ct)
    {
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
                UPDATE workflow_run
                SET status = @status, last_error = @error, updated_at = now()
                WHERE id = @id AND xmin::text::bigint = @expectedXmin
                """;
            update.Parameters.AddWithValue("status", nameof(WorkflowStatus.Failed));
            update.Parameters.AddWithValue("error", errorMessage);
            update.Parameters.AddWithValue("id", runId);
            update.Parameters.AddWithValue("expectedXmin", row.Xmin);

            if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                return false;
            }
        }

        await InsertEventAsync(
            connection, tx, runId, row.CurrentSeq,
            fromNode: row.CurrentNode, toNode: row.CurrentNode,
            status: WorkflowStatus.Failed, awaitingSignal: null,
            outcome: null, variables: null, error: errorMessage,
            kind: nameof(WorkflowEventKind.Failed), ct).ConfigureAwait(false);

        return true;
    }

    private static async Task<bool> CommitAndReturnTrueAsync(NpgsqlTransaction tx, CancellationToken ct)
    {
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async ValueTask CancelAsync(Guid runId, string reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var row = await ReadRunRowAsync(connection, tx, runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Workflow run '{runId}' was not found.");

        if (IsTerminal(row.Status))
        {
            // Idempotent for the same reason as FailAsync above: a run already Succeeded, Failed or
            // Cancelled has nothing left for this call to record, and re-recording it at the same
            // row.CurrentSeq would collide with workflow_run_event's (run_id, seq) unique index.
            return;
        }

        // Same UPDATE-before-INSERT ordering as FailAsync, and for the same reason: two concurrent CancelAsync
        // calls (or a CancelAsync racing a FailAsync) on the same Running run both clear the guard above, and
        // only the UPDATE-first ordering guarantees the loser hits WorkflowConcurrencyException before ever
        // attempting to insert at the same row.CurrentSeq.
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
                UPDATE workflow_run
                SET status = @status, last_error = @reason, updated_at = now()
                WHERE id = @id AND xmin::text::bigint = @expectedXmin
                """;
            update.Parameters.AddWithValue("status", nameof(WorkflowStatus.Cancelled));
            update.Parameters.AddWithValue("reason", reason);
            update.Parameters.AddWithValue("id", runId);
            update.Parameters.AddWithValue("expectedXmin", row.Xmin);

            var affected = await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (affected != 1)
            {
                throw new WorkflowConcurrencyException($"Workflow run '{runId}' was concurrently modified.");
            }
        }

        // WorkflowEventKind has no "Cancelled" member — Advance never produces one, since cancellation is not
        // one of its four evaluation outcomes (see WorkflowEventKind's remarks). The literal string is written
        // directly; the "kind" column is plain text with no check constraint against the enum's members.
        await InsertEventAsync(
            connection, tx, runId, row.CurrentSeq,
            fromNode: row.CurrentNode, toNode: row.CurrentNode,
            status: WorkflowStatus.Cancelled, awaitingSignal: null,
            outcome: null, variables: null, error: reason,
            kind: "Cancelled", ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The most stranded runs <see cref="FindStrandedAsync"/> returns in one call. A dead-lettered dispatch
    /// message strands at most one run apiece, so an unbounded fleet backlog is not an expected shape — this
    /// exists to cap a single sweep's work (the burst of <see cref="FailStrandedAsync"/> calls a caller makes
    /// off one result set) and its one open connection's lifetime, not to model an expected steady-state count.
    /// The scan itself is bounded independently of this constant by migration 1003's partial index,
    /// <c>ix_workflow_run_updated_at_running</c> (<c>ON workflow_run (updated_at) WHERE status = 'Running'</c>):
    /// it covers exactly the rows this query's <c>WHERE</c> can match, so both the filter and the
    /// <c>ORDER BY</c> below resolve to one index scan rather than a sequential scan over every run ever
    /// started, terminal ones included. <see cref="MaxStrandedResults"/> is chosen generously above anything a
    /// healthy system should ever accumulate: a consumer's periodic sweeper (see
    /// <see cref="WorkflowRunReconciler"/>) re-queries every tick, so a backlog past this cap is simply worked
    /// off over a few extra ticks rather than lost.
    /// </summary>
    private const int MaxStrandedResults = 500;

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<WorkflowRun>> FindStrandedAsync(TimeSpan olderThan, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();

        // Awaiting is deliberately excluded: a run parked at an approval gate has nothing in flight by design
        // and may sit there legitimately for days. Sweeping it out because nobody approved it quickly would
        // destroy exactly the work the gate exists to protect. Only Running — a node an in-flight dispatch
        // message was supposed to advance, and might now never will because that message dead-lettered — is a
        // stranded-run candidate. ix_workflow_run_updated_at_running (migration 1003) is a partial index over
        // exactly this predicate, so this filter and the ORDER BY below are backed by one index scan.
        // The threshold is computed from now() — the server clock — not from DateTimeOffset.UtcNow. updated_at
        // is written by the server's own now(), so comparing it against a client-computed instant subtracts two
        // readings of different clocks: an app host running a few minutes fast would find healthy, just-updated
        // runs older than its own threshold and terminate them. Only the interval crosses the wire.
        cmd.CommandText = SelectRunSql + " WHERE status = @running AND updated_at < now() - @olderThan::interval ORDER BY updated_at ASC LIMIT @limit";
        cmd.Parameters.AddWithValue("running", nameof(WorkflowStatus.Running));
        cmd.Parameters.AddWithValue("olderThan", olderThan);
        cmd.Parameters.AddWithValue("limit", MaxStrandedResults);

        var results = new List<WorkflowRun>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(ToWorkflowRun(ReadRow(reader)));
        }

        return results;
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    private IOutboxStore CreateOutboxStore(NpgsqlConnection connection)
    {
        var asyncConnection = connection.AsAsync();
        return _options.OutboxStoreFactory?.Invoke(asyncConnection) ?? new OrmOutboxStore(asyncConnection);
    }

    /// <summary>
    /// The run-update + event-append + conditional-dispatch-enqueue sequence shared by <see cref="CompleteNodeAsync"/>
    /// and <see cref="ResumeAsync"/> once each has its own <see cref="WorkflowTransition"/> in hand.
    /// </summary>
    /// <remarks>
    /// <paramref name="variables"/> is merged into <paramref name="row"/>'s existing bag — later writes win on
    /// key collision — and the merged bag, not a replacement, is what gets persisted: a node that returns no
    /// variables must not wipe what an earlier node wrote. The event log still records <paramref name="variables"/>
    /// unmerged, so <c>workflow_run_event</c> shows exactly what this one transition contributed.
    /// <para>
    /// The xmin-checked <see cref="UpdateRunAsync"/> runs first, before the event <c>INSERT</c> and before the
    /// dispatch enqueue. This is one transaction, so statement order inside it doesn't affect atomicity — only
    /// which failure a losing racer hits first — and that ordering is what matters here: <c>workflow_run_event</c>
    /// carries a unique index on <c>(run_id, seq)</c>. With the <c>UPDATE</c> first, a racer that loses the
    /// <c>xmin</c> check throws <see cref="WorkflowConcurrencyException"/> immediately and never reaches the
    /// event <c>INSERT</c> at all, so two racers can never both attempt to insert an event at the same
    /// <c>(run_id, seq)</c>. With the insert first — the shape this replaced — both racers could insert
    /// successfully and only the loser's later <c>UPDATE</c> would fail, except the second racer's insert
    /// collided on the unique index before ever reaching its own <c>UPDATE</c>, raising a raw
    /// <see cref="Npgsql.PostgresException"/> instead of <see cref="WorkflowConcurrencyException"/>.
    /// </para>
    /// </remarks>
    private async Task ApplyTransitionAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid runId, long seq, RunRow row,
        WorkflowTransition transition, string? outcome, IReadOnlyDictionary<string, object?>? variables, CancellationToken ct)
    {
        var visits = new Dictionary<string, int>(row.Visits, StringComparer.Ordinal);
        if (!string.Equals(transition.NextNode, row.CurrentNode, StringComparison.Ordinal))
        {
            visits[transition.NextNode] = visits.GetValueOrDefault(transition.NextNode) + 1;
        }

        var mergedVariables = new Dictionary<string, object?>(row.Variables, StringComparer.Ordinal);
        if (variables is not null)
        {
            foreach (var (key, value) in variables)
            {
                mergedVariables[key] = value;
            }
        }

        var newSeq = seq + 1;

        await UpdateRunAsync(connection, tx, runId, row.Xmin, newSeq, transition.NextNode, transition.NextStatus, transition.AwaitingSignal, visits, mergedVariables, ct).ConfigureAwait(false);

        await InsertEventAsync(
            connection, tx, runId, seq,
            fromNode: row.CurrentNode, toNode: transition.NextNode,
            status: transition.NextStatus, awaitingSignal: transition.AwaitingSignal,
            outcome: outcome, variables: variables, error: null,
            kind: transition.Kind.ToString(), ct).ConfigureAwait(false);

        if (transition.NextStatus == WorkflowStatus.Running)
        {
            await EnqueueDispatchAsync(connection, tx, runId, newSeq, transition.NextNode, ct).ConfigureAwait(false);
        }
    }

    private async Task EnqueueDispatchAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid runId, long seq, string node, CancellationToken ct)
    {
        var outboxStore = CreateOutboxStore(connection);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new WorkflowDispatchMessage(runId, seq, node));
        await outboxStore.EnqueueAsync(WorkflowDispatch.TypeName, payload, tx, ct).ConfigureAwait(false);
    }

    private static async Task UpdateRunAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid runId, long expectedXmin,
        long newSeq, string currentNode, WorkflowStatus status, string? awaitingSignal,
        IReadOnlyDictionary<string, int> visits, IReadOnlyDictionary<string, object?> variables, CancellationToken ct)
    {
        await using var update = connection.CreateCommand();
        update.Transaction = tx;
        update.CommandText = """
            UPDATE workflow_run
            SET current_node = @currentNode, current_seq = @newSeq, status = @status,
                awaiting_signal = @awaitingSignal, visits = @visits::jsonb, variables = @variables::jsonb, updated_at = now()
            WHERE id = @id AND xmin::text::bigint = @expectedXmin
            """;
        update.Parameters.AddWithValue("currentNode", currentNode);
        update.Parameters.AddWithValue("newSeq", newSeq);
        update.Parameters.AddWithValue("status", status.ToString());
        update.Parameters.AddWithValue("awaitingSignal", (object?)awaitingSignal ?? DBNull.Value);
        update.Parameters.AddWithValue("visits", JsonSerializer.Serialize(visits));
        update.Parameters.AddWithValue("variables", JsonSerializer.Serialize(variables));
        update.Parameters.AddWithValue("id", runId);
        update.Parameters.AddWithValue("expectedXmin", expectedXmin);

        var affected = await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (affected != 1)
        {
            // The zero-row case is the concurrency guard doing its job: another writer's UPDATE already
            // changed this row's xmin since it was read at the start of this operation. Raise rather than let
            // the caller believe a write that never landed actually happened.
            throw new WorkflowConcurrencyException($"Workflow run '{runId}' was concurrently modified.");
        }
    }

    private static async Task InsertEventAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid runId, long seq,
        string? fromNode, string toNode, WorkflowStatus status, string? awaitingSignal,
        string? outcome, IReadOnlyDictionary<string, object?>? variables, string? error,
        string kind, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO workflow_run_event (run_id, seq, kind, from_node, to_node, status, awaiting_signal, outcome, variables, error)
            VALUES (@runId, @seq, @kind, @fromNode, @toNode, @status, @awaitingSignal, @outcome, @variables::jsonb, @error)
            """;
        cmd.Parameters.AddWithValue("runId", runId);
        cmd.Parameters.AddWithValue("seq", seq);
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("fromNode", (object?)fromNode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("toNode", toNode);
        cmd.Parameters.AddWithValue("status", status.ToString());
        cmd.Parameters.AddWithValue("awaitingSignal", (object?)awaitingSignal ?? DBNull.Value);
        cmd.Parameters.AddWithValue("outcome", (object?)outcome ?? DBNull.Value);
        cmd.Parameters.AddWithValue("variables", variables is null ? DBNull.Value : JsonSerializer.Serialize(variables));
        cmd.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
        try
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (string.Equals(ex.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal) && string.Equals(ex.ConstraintName, "ix_workflow_run_event_run_id_seq", StringComparison.Ordinal))
        {
            // Defence in depth: every current caller of this method is safe from the race this mapping
            // guards against, for two different reasons. ApplyTransitionAsync, FailAsync, and CancelAsync
            // each run their xmin-checked UPDATE before this INSERT specifically so a losing racer throws
            // WorkflowConcurrencyException there and never reaches this statement at all: two racers can no
            // longer both attempt to insert an event at the same (run_id, seq). StartAsync is safe for an
            // unrelated reason — it inserts against a freshly generated Guid, so a (run_id, seq) collision is
            // structurally impossible there, and a concurrent duplicate start is absorbed by
            // "ON CONFLICT (correlation_key) DO NOTHING" before this insert is ever reached. Unreachable
            // through any of today's call sites; kept in case a future caller inserts an event without either
            // safeguard protecting it.
            throw new WorkflowConcurrencyException($"Workflow run '{runId}' already has an event recorded at seq {seq}.", ex);
        }
    }

    private static async Task<RunRow?> ReadRunRowAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid runId, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = SelectRunSql + " WHERE id = @id";
        cmd.Parameters.AddWithValue("id", runId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRow(reader) : null;
    }

    private static RunRow ReadRow(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        Process: reader.GetString(1),
        ProcessVersion: reader.GetInt32(2),
        CorrelationKey: reader.GetString(3),
        CurrentNode: reader.GetString(4),
        CurrentSeq: reader.GetInt64(5),
        Status: Enum.Parse<WorkflowStatus>(reader.GetString(6)),
        AwaitingSignal: reader.IsDBNull(7) ? null : reader.GetString(7),
        Visits: JsonSerializer.Deserialize<Dictionary<string, int>>(reader.GetString(8)) ?? [],
        Variables: DeserializeVariables(reader.GetString(9)),
        LastError: reader.IsDBNull(10) ? null : reader.GetString(10),
        Xmin: reader.GetInt64(11));

    /// <summary>
    /// Deserializing straight to <c>Dictionary&lt;string, object?&gt;</c> leaves every value a boxed
    /// <see cref="JsonElement"/>, not the plain CLR value a consumer reading <see cref="WorkflowRun.Variables"/>
    /// would expect — comparing a boxed <see cref="JsonElement"/> string against a bare <see cref="string"/>
    /// never succeeds. Parses to a <see cref="JsonElement"/> instead and hands it to
    /// <c>WorkflowVariableBlock.ReadObject</c>, so the bag holds ordinary strings, numbers, booleans, nulls,
    /// dictionaries and lists.
    /// </summary>
    /// <remarks>
    /// That method, and not a copy of it here, on purpose: it is also what <c>WorkflowNodeDispatcher</c> unwraps
    /// a reported variables object with on the way <em>in</em>. Two copies would let a value come back out of
    /// this column as a different CLR type than it went in as — which is precisely what happened while the
    /// unwrapping did live in two places.
    /// </remarks>
    private static Dictionary<string, object?> DeserializeVariables(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
            ? WorkflowVariableBlock.ReadObject(document.RootElement)
            : new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    /// <summary>Whether <paramref name="status"/> is one a run cannot leave: no further transition, completion, or termination is meaningful once reached.</summary>
    private static bool IsTerminal(WorkflowStatus status) =>
        status is WorkflowStatus.Succeeded or WorkflowStatus.Failed or WorkflowStatus.Cancelled;

    private static WorkflowRun ToWorkflowRun(RunRow row) => new()
    {
        Id = row.Id,
        Process = row.Process,
        ProcessVersion = row.ProcessVersion,
        CurrentNode = row.CurrentNode,
        CurrentSeq = row.CurrentSeq,
        Status = row.Status,
        AwaitingSignal = row.AwaitingSignal,
        Visits = row.Visits,
        Variables = row.Variables,
        LastError = row.LastError,
    };

    private sealed record RunRow(
        Guid Id,
        string Process,
        int ProcessVersion,
        string CorrelationKey,
        string CurrentNode,
        long CurrentSeq,
        WorkflowStatus Status,
        string? AwaitingSignal,
        Dictionary<string, int> Visits,
        Dictionary<string, object?> Variables,
        string? LastError,
        long Xmin);
}
