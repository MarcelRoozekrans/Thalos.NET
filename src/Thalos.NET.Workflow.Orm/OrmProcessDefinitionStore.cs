using System.Security.Cryptography;
using System.Text;
using Npgsql;
using ZeroAlloc.Results;

namespace Thalos.Workflow.Orm;

/// <summary>
/// PostgreSQL-backed <see cref="IProcessDefinitionStore"/>: raw ADO.NET (no EF Core) over <c>process_definition</c>,
/// mirroring <see cref="OrmWorkflowStore"/>'s shape. Shares <see cref="WorkflowOrmOptions"/> with
/// <see cref="OrmWorkflowStore"/> — both open a connection against the same <see cref="WorkflowOrmOptions.ConnectionString"/>
/// per call, and <see cref="TryRemoveAsync"/> reads <c>workflow_run</c> directly, in the same database, to check
/// whether a run still pins the version being removed.
/// </summary>
public sealed class OrmProcessDefinitionStore(WorkflowOrmOptions options) : IProcessDefinitionStore
{
    private readonly WorkflowOrmOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc/>
    public async ValueTask<Result> UpsertAndActivateAsync(ProcessDefinition definition, string yaml, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(yaml);

        var contentHash = ComputeContentHash(yaml);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await LockProcessAsync(connection, tx, definition.Name, ct).ConfigureAwait(false);

        // The immutability check and the write are one statement on purpose. A read-then-write pair would leave a
        // window where two syncs both see "no row yet" and the second silently overwrites the first; here the
        // conflicting row is locked by ON CONFLICT itself, so the comparison happens against the committed row.
        // The DO UPDATE's WHERE is what enforces the rule: it fires only when the stored hash equals the incoming
        // one, so an identical re-sync updates the row to itself and RETURNING yields a row, while a changed
        // definition matches nothing, writes nothing, and yields no row at all. Distinguishing the two therefore
        // needs no second query and cannot race.
        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText = """
                INSERT INTO process_definition (process, version, yaml, content_hash, is_active)
                VALUES (@process, @version, @yaml, @content_hash, false)
                ON CONFLICT (process, version) DO UPDATE SET yaml = EXCLUDED.yaml
                WHERE process_definition.content_hash = EXCLUDED.content_hash
                RETURNING content_hash
                """;
            upsert.Parameters.AddWithValue("process", definition.Name);
            upsert.Parameters.AddWithValue("version", definition.Version);
            upsert.Parameters.AddWithValue("yaml", yaml);
            upsert.Parameters.AddWithValue("content_hash", contentHash);

            if (await upsert.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
            {
                // Nothing was written — the transaction is abandoned without committing, so the stored
                // definition is byte-for-byte what it was before this call.
                return Result.Failure(
                    $"Process '{definition.Name}' version {definition.Version} is already stored with different content. A stored version is immutable, because a run that started on it must keep the exact graph it started on — bump the version instead of editing version {definition.Version} in place.");
            }
        }

        await ActivateAsync(connection, tx, definition, ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>
    /// The advisory-lock class this package takes per-process activation locks under. Advisory locks live in one
    /// cluster-wide space shared with everything else using the database, including ZeroAlloc.ORM's own migration
    /// lock; namespacing with a fixed class keeps a key collision confined to this package's own locks, where the
    /// worst outcome is serialising two activations that need not have been.
    /// </summary>
    private const int ActivationLockClass = 0x5448414C;

    /// <summary>
    /// Serialises activation of one process for the rest of the transaction. Released automatically on commit or
    /// rollback — there is no unlock path to get wrong, and an abandoned transaction cannot strand the lock.
    /// </summary>
    /// <remarks>
    /// Taken before the upsert rather than before the activation, and that ordering is the whole point. Row locks
    /// alone cannot be acquired in one canonical order here: the upsert's <c>ON CONFLICT</c> already locks the
    /// target version's row, so a sync activating version 1 and one activating version 3 each reach the
    /// activation step already holding a different row, and ordering the activation's own locking only moves the
    /// deadlock instead of removing it — measured, not assumed. One lock per process, taken before any row is
    /// touched, removes the question: two activations of the same process cannot interleave, and two activations
    /// of different processes contend only on a rare key collision, whose sole cost is the serialisation
    /// described above. Activation is a sync-time administrative operation, not a hot path, so serialising it per
    /// process costs nothing worth measuring.
    /// <para>
    /// <b>Scope: activation only, not every write to the table.</b> <see cref="TryRemoveAsync"/> deliberately
    /// does not take this lock, even though it deletes rows and the row it deletes may be the active one. So a
    /// removal running concurrently with an activation is not serialised against it, and the pair could in
    /// principle leave the process with <em>zero</em> active versions — no deadlock, and no violation of the
    /// at-most-one invariant the index enforces, but not the at-least-one a reader might assume this lock
    /// provides. Do not read this as a per-process write lock; it is a per-process activation lock.
    /// </para>
    /// <para>
    /// <c>hashtext</c> is an undocumented PostgreSQL internal. It is safe here because its value is only ever
    /// compared against itself, within one server, for the lifetime of a transaction — nothing persists it or
    /// carries it between servers or versions. Treat its output as an opaque, non-portable token, and do not
    /// build anything on the specific numbers it produces.
    /// </para>
    /// </remarks>
    private static async Task LockProcessAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string process, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT pg_advisory_xact_lock(@lock_class, hashtext(@process))";
        cmd.Parameters.AddWithValue("lock_class", ActivationLockClass);
        cmd.Parameters.AddWithValue("process", process);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes <paramref name="definition"/>'s version the process's only active row: deactivate every other
    /// version first, then activate this one. Two statements in one transaction, deliberately, and in that order.
    /// </summary>
    /// <remarks>
    /// This was one statement — <c>SET is_active = (version = @version) WHERE process = @process</c> — on the
    /// reasoning that a single UPDATE could not leave two versions active even transiently. That reasoning was
    /// wrong about how the guard behaves: <c>ix_process_definition_one_active_per_process</c> is a partial unique
    /// <em>index</em>, which PostgreSQL checks per row as the UPDATE walks them, not once at statement end. So
    /// whenever the walk reached the version being activated before the one being deactivated, the row set
    /// briefly held two active versions and the write failed with <c>23505</c>. It happened to work while
    /// activation only ever followed an insert of a brand-new version, and broke the moment a version already
    /// stored was re-activated — a rollback to a previous version, or a restart re-syncing an unchanged file
    /// after a newer version had been activated. A partial unique index cannot be made <c>DEFERRABLE</c>, so
    /// splitting the statement is the fix: deactivating first passes through a state with <em>zero</em> active
    /// rows, which the index is perfectly happy with, and the invariant still holds at commit because both
    /// statements share this transaction.
    /// <para>
    /// <b>Why the deactivate carries no <c>is_active</c> predicate.</b> Splitting the statement introduced a
    /// second race the single statement did not have, and the predicate was its trigger. Two concurrent syncs of
    /// one process at different versions, version 2 active: A activates version 1, B activates version 3, A
    /// commits first. Under <c>READ COMMITTED</c> B's snapshot predates that commit, so B still sees version 1 as
    /// inactive — and <c>AND is_active</c> filters version 1 out of B's scan entirely. A row never scanned gets no
    /// <c>EPQ</c> re-check, so B never deactivates it and B's own activate collides with A's committed active row:
    /// a raw <c>23505</c> out of a <c>ValueTask&lt;Result&gt;</c> API, through <c>ProcessDefinitionSync</c>, which
    /// has no <c>catch</c> for it. Dropping the predicate keeps that row in B's scan so the <c>EPQ</c> re-check
    /// can re-evaluate it, at the cost of a few no-op row versions per activation.
    /// </para>
    /// <para>
    /// <b>That alone was not enough, and the code does not pretend otherwise.</b> Removing the predicate turned
    /// the <c>23505</c> into <c>40P01</c> — see the next paragraph — so the guarantee actually rests on
    /// <see cref="LockProcessAsync"/>. With activation serialised per process, this predicate is no longer
    /// load-bearing: the concurrency test passes with it either present or absent. It stays absent because a
    /// statement that is correct without relying on an outer lock is worth more than one that is not, but the
    /// honest account is that serialisation is the fix and this is belt-and-braces.
    /// </para>
    /// <para>
    /// <b>Concurrency is resolved before this method runs, not inside it.</b> Dropping the predicate fixed the
    /// <c>23505</c> and exposed the next layer: splitting one statement into two also split the <em>lock
    /// order</em>. Activating version 1 deactivates 2 and 3 then locks 1; activating version 3 deactivates 1 and
    /// 2 then locks 3 — run concurrently, the first holds 3 and wants 1 while the second holds 1 and wants 3,
    /// which is <c>40P01</c>: an observed deadlock, not a theoretical one. The original single statement avoided
    /// that for the same reason it caused the other bug — one scan over every row meant both transactions took
    /// the same locks in the same order. That canonical order cannot be rebuilt here, because the upsert has
    /// already locked the target row by the time this method is reached, so
    /// <see cref="LockProcessAsync"/> resolves it upstream of all of this instead.
    /// </para>
    /// </remarks>
    private static async Task ActivateAsync(NpgsqlConnection connection, NpgsqlTransaction tx, ProcessDefinition definition, CancellationToken ct)
    {
        await using (var deactivate = connection.CreateCommand())
        {
            deactivate.Transaction = tx;
            // No "AND is_active" predicate, deliberately — see this method's remarks. With LockProcessAsync in
            // place this is defence in depth rather than the mechanism: it keeps the statement correct on its own
            // terms, without depending on the serialisation to hide a snapshot-visibility trap.
            deactivate.CommandText = """
                UPDATE process_definition SET is_active = false
                WHERE process = @process AND version <> @version
                """;
            deactivate.Parameters.AddWithValue("process", definition.Name);
            deactivate.Parameters.AddWithValue("version", definition.Version);
            await deactivate.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var activate = connection.CreateCommand())
        {
            activate.Transaction = tx;
            activate.CommandText = """
                UPDATE process_definition SET is_active = true
                WHERE process = @process AND version = @version
                """;
            activate.Parameters.AddWithValue("process", definition.Name);
            activate.Parameters.AddWithValue("version", definition.Version);
            await activate.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The content identity a stored version is pinned to: lowercase hex of SHA-256 over <paramref name="yaml"/>'s
    /// UTF-8 bytes. Migration 1004's backfill computes the same value in SQL, and the two must agree exactly — a
    /// mismatch would make the first re-sync of an unchanged file read as a content change and be refused. Not a
    /// security boundary, so the choice of SHA-256 is about collision resistance for accidental edits, not about
    /// resisting a crafted one.
    /// </summary>
    private static string ComputeContentHash(string yaml) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(yaml)));

    /// <inheritdoc/>
    public async ValueTask<int?> GetActiveVersionAsync(string process, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(process);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT version FROM process_definition WHERE process = @process AND is_active";
        cmd.Parameters.AddWithValue("process", process);
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? null : (int)value;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Reads the <em>exact</em> (process, version) row, with no <c>is_active</c> filter: a run pinned to a
    /// version that has since been superseded by a newer activation must keep resolving to the shape it started
    /// on, so filtering on <c>is_active</c> here would break every live run the moment a new version activated —
    /// precisely the drift <see cref="UpsertAndActivateAsync"/> is careful not to cause.
    /// </remarks>
    public async ValueTask<Result<ProcessDefinition>> GetAsync(string process, int version, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(process);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT yaml FROM process_definition WHERE process = @process AND version = @version";
        cmd.Parameters.AddWithValue("process", process);
        cmd.Parameters.AddWithValue("version", version);

        var yaml = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (yaml is null)
        {
            return Result<ProcessDefinition>.Failure(
                $"No process definition stored for '{process}' version {version}.");
        }

        // Parsed here rather than trusted: the row was written by ProcessDefinitionSync after
        // ProcessValidator accepted it, but this store is not the only thing that can reach the table, and a
        // row that no longer parses must surface as a named failure rather than an exception out of a read.
        var loaded = ProcessLoader.Load(yaml);
        return loaded.IsSuccess
            ? loaded
            : Result<ProcessDefinition>.Failure(
                $"The stored definition for '{process}' version {version} could not be parsed: {loaded.Error}");
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Deliberately does not require a <c>process_definition</c> row to exist for <paramref name="version"/>:
    /// <see cref="IWorkflowStore.StartAsync"/> stamps <see cref="WorkflowRun.ProcessVersion"/> onto a run
    /// independently of this store, so a run can pin a version this store never held a row for (or has already
    /// removed the row of). Removing a version with no stored row is a no-op success, the same as removing an
    /// entry that was already gone — the property this method enforces is purely "does a non-terminal run still
    /// need this shape", never "does a row happen to exist".
    /// </remarks>
    public async ValueTask<Result> TryRemoveAsync(string process, int version, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(process);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var pinned = await CountPinningRunsAsync(connection, tx, process, version, ct).ConfigureAwait(false);
        if (pinned > 0)
        {
            return Result.Failure($"Process '{process}' version {version} is still pinned by {pinned} run(s) that have not reached a terminal status.");
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM process_definition WHERE process = @process AND version = @version";
            delete.Parameters.AddWithValue("process", process);
            delete.Parameters.AddWithValue("version", version);
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>
    /// The pin this store exists to enforce: a run started against this exact (process, version) pair keeps
    /// <see cref="WorkflowRun.ProcessVersion"/> fixed at that value for its whole life
    /// (<see cref="IWorkflowStore.StartAsync"/> captures it once and nothing ever changes it), so a run that has
    /// not yet reached a terminal status needs this shape resolvable for as long as it runs. Filtered on
    /// <c>process_version</c>, not just <c>process</c>: a run pinned to a <em>different</em> version of the same
    /// process must never block removing this one — dropping this filter is exactly the regression
    /// <c>Thalos.Tests.Workflow.Orm.ProcessDefinitionStoreTests</c> exercises.
    /// </summary>
    private static async Task<long> CountPinningRunsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string process, int version, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT count(*) FROM workflow_run
            WHERE process = @process AND process_version = @version
              AND status NOT IN (@succeeded, @failed, @cancelled)
            """;
        cmd.Parameters.AddWithValue("process", process);
        cmd.Parameters.AddWithValue("version", version);
        cmd.Parameters.AddWithValue("succeeded", nameof(WorkflowStatus.Succeeded));
        cmd.Parameters.AddWithValue("failed", nameof(WorkflowStatus.Failed));
        cmd.Parameters.AddWithValue("cancelled", nameof(WorkflowStatus.Cancelled));
        return (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }
}
