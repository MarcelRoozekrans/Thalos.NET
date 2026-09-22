using System.Data.Async.Adapters;
using System.Text.Json;
using Npgsql;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.Orm;
using ZeroAlloc.Results;

namespace Thalos.Workflow.Orm;

/// <summary>
/// PostgreSQL-backed <see cref="IWorkflowStore"/>: raw ADO.NET (no EF Core) over <c>workflow_run</c> and the
/// append-only <c>workflow_run_event</c>, with the post-transition dispatch message enqueued through
/// <c>ZeroAlloc.Outbox.Orm.OrmOutboxStore</c> in the same <see cref="System.Data.Common.DbTransaction"/> as the
/// event append and the run update — one commit or none, so a run never records a transition without the work
/// behind its new node also scheduled, and never schedules that work without the transition having landed.
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
public sealed class OrmWorkflowStore(WorkflowOrmOptions options) : IWorkflowStore
{
    private const string DispatchMessageTypeName = "thalos.workflow.node-dispatch";

    private const string SelectRunSql = """
        SELECT id, process, process_version, correlation_key, current_node, current_seq, status, awaiting_signal, visits, variables, last_error, xmin::text::bigint AS xmin
        FROM workflow_run
        """;

    private readonly WorkflowOrmOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc/>
    public async ValueTask<Guid> StartAsync(string process, int version, string correlationKey, string startNode, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(process);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(startNode);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var id = Guid.NewGuid();
        var visitsJson = JsonSerializer.Serialize(new Dictionary<string, int>(StringComparer.Ordinal) { [startNode] = 1 });

        int inserted;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO workflow_run (id, process, process_version, correlation_key, current_node, current_seq, status, awaiting_signal, visits, variables, last_error)
                VALUES (@id, @process, @version, @correlationKey, @currentNode, 1, @status, NULL, @visits::jsonb, '{}'::jsonb, NULL)
                ON CONFLICT (correlation_key) DO NOTHING
                """;
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("process", process);
            insert.Parameters.AddWithValue("version", version);
            insert.Parameters.AddWithValue("correlationKey", correlationKey);
            insert.Parameters.AddWithValue("currentNode", startNode);
            insert.Parameters.AddWithValue("status", nameof(WorkflowStatus.Running));
            insert.Parameters.AddWithValue("visits", visitsJson);
            inserted = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (inserted == 0)
        {
            // Idempotent lookup: a run for this correlation key is already in flight. Return its id rather
            // than create a duplicate — no event is appended, because no new run was entered.
            await using var select = connection.CreateCommand();
            select.Transaction = tx;
            select.CommandText = "SELECT id FROM workflow_run WHERE correlation_key = @correlationKey";
            select.Parameters.AddWithValue("correlationKey", correlationKey);
            var existing = (Guid)(await select.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return existing;
        }

        await InsertEventAsync(
            connection, tx, id, seq: 1,
            fromNode: null, toNode: startNode,
            status: WorkflowStatus.Running, awaitingSignal: null,
            outcome: null, variables: null, error: null,
            kind: nameof(WorkflowEventKind.Entered), ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return id;
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

        if (!_options.Processes.TryGetValue((row.Process, row.ProcessVersion), out var process))
        {
            return Result.Failure($"No process definition registered for '{row.Process}' version {row.ProcessVersion}.");
        }

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

        await ApplyTransitionAsync(connection, tx, runId, row.CurrentSeq, row, transitionResult.Value, outcome: null, variables, ct).ConfigureAwait(false);

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

        await InsertEventAsync(
            connection, tx, runId, row.CurrentSeq,
            fromNode: row.CurrentNode, toNode: row.CurrentNode,
            status: WorkflowStatus.Failed, awaitingSignal: null,
            outcome: null, variables: null, error: errorMessage,
            kind: nameof(WorkflowEventKind.Failed), ct).ConfigureAwait(false);

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

            var affected = await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (affected != 1)
            {
                throw new WorkflowConcurrencyException($"Workflow run '{runId}' was concurrently modified.");
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask CancelAsync(Guid runId, string reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var row = await ReadRunRowAsync(connection, tx, runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Workflow run '{runId}' was not found.");

        // WorkflowEventKind has no "Cancelled" member — Advance never produces one, since cancellation is not
        // one of its four evaluation outcomes (see WorkflowEventKind's remarks). The literal string is written
        // directly; the "kind" column is plain text with no check constraint against the enum's members.
        await InsertEventAsync(
            connection, tx, runId, row.CurrentSeq,
            fromNode: row.CurrentNode, toNode: row.CurrentNode,
            status: WorkflowStatus.Cancelled, awaitingSignal: null,
            outcome: null, variables: null, error: reason,
            kind: "Cancelled", ct).ConfigureAwait(false);

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

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<WorkflowRun>> FindStrandedAsync(TimeSpan olderThan, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectRunSql + " WHERE status IN (@running, @awaiting) AND updated_at < @threshold";
        cmd.Parameters.AddWithValue("running", nameof(WorkflowStatus.Running));
        cmd.Parameters.AddWithValue("awaiting", nameof(WorkflowStatus.Awaiting));
        cmd.Parameters.AddWithValue("threshold", DateTimeOffset.UtcNow - olderThan);

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
    /// The event-append + run-update + conditional-dispatch-enqueue sequence shared by <see cref="CompleteNodeAsync"/>
    /// and <see cref="ResumeAsync"/> once each has its own <see cref="WorkflowTransition"/> in hand.
    /// </summary>
    /// <remarks>
    /// <paramref name="variables"/> is merged into <paramref name="row"/>'s existing bag — later writes win on
    /// key collision — and the merged bag, not a replacement, is what gets persisted: a node that returns no
    /// variables must not wipe what an earlier node wrote. The event log still records <paramref name="variables"/>
    /// unmerged, so <c>workflow_run_event</c> shows exactly what this one transition contributed.
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

        await InsertEventAsync(
            connection, tx, runId, seq,
            fromNode: row.CurrentNode, toNode: transition.NextNode,
            status: transition.NextStatus, awaitingSignal: transition.AwaitingSignal,
            outcome: outcome, variables: variables, error: null,
            kind: transition.Kind.ToString(), ct).ConfigureAwait(false);

        await UpdateRunAsync(connection, tx, runId, row.Xmin, newSeq, transition.NextNode, transition.NextStatus, transition.AwaitingSignal, visits, mergedVariables, ct).ConfigureAwait(false);

        if (transition.NextStatus == WorkflowStatus.Running)
        {
            await EnqueueDispatchAsync(connection, tx, runId, newSeq, transition.NextNode, ct).ConfigureAwait(false);
        }
    }

    private async Task EnqueueDispatchAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid runId, long seq, string node, CancellationToken ct)
    {
        var outboxStore = CreateOutboxStore(connection);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new WorkflowDispatchMessage(runId, seq, node));
        await outboxStore.EnqueueAsync(DispatchMessageTypeName, payload, tx, ct).ConfigureAwait(false);
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
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
    /// never succeeds. Deserializes through <see cref="JsonElement"/> instead and unwraps each value with
    /// <see cref="ToPlainValue"/> so the bag holds ordinary strings, numbers, booleans, nulls, dictionaries and
    /// lists.
    /// </summary>
    private static Dictionary<string, object?> DeserializeVariables(string json)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];
        var result = new Dictionary<string, object?>(raw.Count, StringComparer.Ordinal);
        foreach (var (key, element) in raw)
        {
            result[key] = ToPlainValue(element);
        }

        return result;
    }

    private static object? ToPlainValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var longValue) ? longValue : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToPlainValue(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(ToPlainValue).ToList(),
        _ => null,
    };

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

    private sealed record WorkflowDispatchMessage(Guid RunId, long Seq, string Node);
}
