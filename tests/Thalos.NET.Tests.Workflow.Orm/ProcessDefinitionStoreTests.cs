using Npgsql;
using Thalos.Workflow;
using Thalos.Workflow.Orm;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>
/// Tests for the three properties Task 6 exists to establish: a definition failing validation is rejected and
/// the previous version stays active; a version stays resolvable while any run pins it; and an in-flight run
/// keeps its pinned version when a newer one activates. <c>ValidV1</c>/<c>ValidV2</c> are test fixtures covering
/// all four graph primitives (sequence, branch, loop-back, approval gate) — not a real manufacturing process.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Docker")]
public sealed class ProcessDefinitionStoreTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string ValidV1 = """
        process: manufacture
        version: 1
        nodes:
          implement: { agent: backend, skill: tdd, next: review }
          review:
            agent: reviewer
            skill: code-review
            outcomes: [approved, rejected]
            branch: { approved: gate, rejected: implement }
            maxVisits: 5
            onExceeded: adjudicate
          gate: { await: human_approval, next: publish }
          publish: { agent: publisher, skill: finish, next: done }
          adjudicate: { agent: lead, skill: adjudicate, next: done }
          done: { terminal: succeeded }
        """;

    private const string ValidV2 = """
        process: manufacture
        version: 2
        nodes:
          implement: { agent: backend, skill: tdd, next: review }
          review:
            agent: reviewer
            skill: code-review
            outcomes: [approved, rejected]
            branch: { approved: done, rejected: implement }
          done: { terminal: succeeded }
        """;

    private OrmWorkflowStore _store = null!;
    private OrmProcessDefinitionStore _definitions = null!;
    private ProcessDefinitionSync _sync = null!;
    private FakeProcessDefinitionSource _source = null!;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        var options = new WorkflowOrmOptions { ConnectionString = pg.ConnectionString };
        _definitions = new OrmProcessDefinitionStore(options);
        _store = new OrmWorkflowStore(options, _definitions);
        _source = new FakeProcessDefinitionSource();
        _sync = new ProcessDefinitionSync(_source, _definitions, new AlwaysResolves());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // --- Property 1: a definition failing validation is rejected; the previous version stays active --------

    [Fact]
    public async Task An_invalid_definition_is_rejected_and_the_previous_version_stays_active()
    {
        var ct = CancellationToken.None;
        await WriteProcessFile(ValidV1);
        await _sync.SyncAsync(ct);

        await WriteProcessFile(ValidV1.Replace("next: review", "next: no_such_node"));
        var result = await _sync.SyncAsync(ct);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("no_such_node");
        var active = await ActiveVersion("manufacture");
        active.Should().Be(1, "a broken edit must never become runnable");
    }

    // --- Property 3: an in-flight run keeps the version it started on ----------------------------------------

    [Fact]
    public async Task An_in_flight_run_keeps_the_version_it_started_on_when_a_new_one_activates()
    {
        var ct = CancellationToken.None;
        await WriteProcessFile(ValidV1);
        await _sync.SyncAsync(ct);
        var runId = await _store.StartAsync("manufacture", version: 1, "c1", "implement", initialVariables: null, ct);

        await WriteProcessFile(ValidV2); // a valid, different shape
        await _sync.SyncAsync(ct);

        (await _store.FindAsync(runId, ct))!.ProcessVersion.Should().Be(1);
        (await ActiveVersion("manufacture")).Should().Be(2, "new runs take the new shape");
    }

    // --- Property 2: a version stays resolvable while any run pins it ----------------------------------------

    [Fact]
    public async Task A_version_cannot_be_removed_while_a_run_still_pins_it()
    {
        var ct = CancellationToken.None;
        var runId = await _store.StartAsync("manufacture", 1, "c1", "implement", initialVariables: null, ct);
        await WriteProcessFile(ValidV2);
        await _sync.SyncAsync(ct);

        (await _definitions.TryRemoveAsync("manufacture", version: 1, ct)).IsFailure.Should().BeTrue();

        await _store.CancelAsync(runId, "test", ct);
        (await _definitions.TryRemoveAsync("manufacture", version: 1, ct)).IsSuccess.Should().BeTrue();
    }

    // --- Falsifiability: the retention check above only ever exercises one version, because it is the only
    // one a run ever pins in that test. This test adds a second, concurrently pinned version of the *same*
    // process, so a version filter dropped from the pin-check query (grouping by process alone) would block
    // removing version 1 even after its own run finished, purely because version 2's run is still live. See
    // this task's report for the manual red/green demonstration against CountPinningRunsAsync.
    [Fact]
    public async Task Removing_one_version_is_unaffected_by_a_different_version_still_being_pinned()
    {
        var ct = CancellationToken.None;
        var v1RunId = await _store.StartAsync("manufacture", 1, "c-v1", "implement", initialVariables: null, ct);
        await _store.StartAsync("manufacture", 2, "c-v2", "implement", initialVariables: null, ct); // stays Running for the whole test

        await _store.CancelAsync(v1RunId, "test", ct);

        (await _definitions.TryRemoveAsync("manufacture", version: 1, ct)).IsSuccess.Should().BeTrue(
            "version 1's own run is terminal; version 2's still-running run must not block removing version 1");
        (await _definitions.TryRemoveAsync("manufacture", version: 2, ct)).IsFailure.Should().BeTrue(
            "version 2's run is still running and must still block removing version 2");
    }

    // --- Property 4: a stored version is immutable ---------------------------------------------------------

    /// <summary>
    /// Re-syncing unchanged files is what happens on every host startup, so it has to stay a clean no-op — and,
    /// critically, it must still <em>activate</em>. Version 2 is activated in between specifically so the final
    /// assertion has somewhere to be wrong: asserting version 1 is active immediately after syncing version 1
    /// would hold just as well against an implementation that returns early and never activates anything, which
    /// is the failure this test exists to catch — a restart re-syncing unchanged files and leaving the wrong
    /// version active, or none. Going back to version 1 is also the rollback path, so this covers both at once.
    /// </summary>
    /// <remarks>
    /// Turns red if the identical-content branch skips activation, and separately if the check is written as
    /// "reject any re-store of an existing version" rather than "reject a re-store whose content differs" — that
    /// second mistake fails the success assertion instead. See this task's report for the red observation.
    /// </remarks>
    [Fact]
    public async Task Re_syncing_identical_content_at_the_same_version_re_activates_that_version()
    {
        var ct = CancellationToken.None;
        await WriteProcessFile(ValidV1);
        (await _sync.SyncAsync(ct)).IsSuccess.Should().BeTrue();

        await WriteProcessFile(ValidV2);
        (await _sync.SyncAsync(ct)).IsSuccess.Should().BeTrue();
        (await ActiveVersion("manufacture")).Should().Be(2, "version 2 must genuinely be the active one before the rollback, or the final assertion proves nothing");

        // Byte-for-byte the document version 1 was first stored from — the rollback case, and the same thing a
        // restart does over a process file that has not changed.
        await WriteProcessFile(ValidV1);
        var rolledBack = await _sync.SyncAsync(ct);

        rolledBack.IsSuccess.Should().BeTrue(rolledBack.IsFailure ? rolledBack.Error : "");
        rolledBack.Value.Should().Be(1, "the unchanged document is still activated, not skipped");
        (await ActiveVersion("manufacture")).Should().Be(1, "an identical re-sync must still activate — otherwise a restart over unchanged files leaves the wrong version active");
        (await StoredYaml("manufacture", 1)).Should().Be(ValidV1, "re-storing identical content must not disturb what is stored");
    }

    /// <summary>
    /// The invariant that makes a version number a real promise. A valid edit that reuses a version number already
    /// stored with different content is refused, and — the assertion that actually matters — the stored definition
    /// is left byte-for-byte as it was. An error return with the row already rewritten would be worse than no
    /// check at all: the caller would believe nothing happened while a live run's graph had already changed.
    /// </summary>
    /// <remarks>
    /// Turns red if <c>UpsertAndActivateAsync</c> goes back to an unconditional <c>DO UPDATE SET yaml</c>, or if
    /// the rejection is implemented as a read-then-write pair whose write still lands before the check fails.
    /// </remarks>
    [Fact]
    public async Task Re_syncing_changed_content_at_the_same_version_is_refused_and_leaves_the_stored_definition_unchanged()
    {
        var ct = CancellationToken.None;
        await WriteProcessFile(ValidV1);
        (await _sync.SyncAsync(ct)).IsSuccess.Should().BeTrue();
        var runId = await _store.StartAsync("manufacture", version: 1, "c-immutable", "implement", initialVariables: null, ct);

        // Still version 1, still perfectly valid, but a different graph: 'review' rejecting now loops to
        // 'adjudicate' instead of back to 'implement'. Exactly the edit that must not land underneath the run
        // started above.
        var edited = ValidV1.Replace("branch: { approved: gate, rejected: implement }", "branch: { approved: gate, rejected: adjudicate }");
        edited.Should().NotBe(ValidV1, "the fixture edit must actually change the document, or this test proves nothing");
        await WriteProcessFile(edited);

        var result = await _sync.SyncAsync(ct);

        result.IsFailure.Should().BeTrue("a stored version is immutable — the author has to bump the version");
        result.Error.Should().Contain("manufacture").And.Contain("1");
        (await StoredYaml("manufacture", 1)).Should().Be(
            ValidV1,
            "the refusal must leave the stored definition byte-for-byte unchanged — an error return with the row already rewritten would be worse than no check at all");
        (await _store.FindAsync(runId, ct))!.ProcessVersion.Should().Be(1);

        // And the run still resolves the graph it started on.
        var resolved = await _definitions.GetAsync("manufacture", 1, ct);
        resolved.IsSuccess.Should().BeTrue(resolved.IsFailure ? resolved.Error : "");
        resolved.Value.Nodes["review"].Branch["rejected"].Should().Be("implement", "the live run's graph must be the one it started on, not the refused edit");
    }

    /// <summary>
    /// Two syncs of one process activating <em>different</em> versions at the same time. <b>Both must succeed</b>
    /// — that is the contract this test pins, and what <see cref="ActivateAsync"/> asserts. Serialised by
    /// <c>LockProcessAsync</c>, the two activations run one after the other rather than racing, and neither has
    /// anything to fail on: <see cref="IProcessDefinitionStore.UpsertAndActivateAsync"/> returns
    /// <see cref="Result.Failure"/> only for the immutability check, which needs the same version with different
    /// content and so cannot arise here. Last writer wins. Above all, neither may <em>throw</em>: a raw
    /// <c>PostgresException</c> out of a <c>ValueTask&lt;Result&gt;</c> API would escape
    /// <see cref="ProcessDefinitionSync"/> entirely, which has no <c>catch</c> for one. Exactly one version must
    /// end active, the invariant the partial unique index exists for.
    /// </summary>
    /// <remarks>
    /// This is the test the "cannot race" claim was missing: that property was asserted only in prose while the
    /// activation was one statement, and stayed prose when it became two.
    /// <para>
    /// <b>It guards <c>LockProcessAsync</c>.</b> The change that turns it red is removing the per-process
    /// advisory lock, and it goes red with <c>40P01: deadlock detected</c> — observed, not predicted. It is
    /// <em>not</em> a guard on the deactivate's <c>AND is_active</c> predicate: with the lock held, restoring
    /// that predicate cannot produce a <c>23505</c>, because the lock blocks the second transaction at its very
    /// first statement, before it takes any snapshot, so once the first commits and releases, the second's
    /// deactivate runs on a fresh <c>READ COMMITTED</c> snapshot that already contains the committed row and
    /// keeps it in scan. The test passes with the predicate present or absent. Saying otherwise would credit a
    /// statement with a safety something else provides, which is the precise mistake that produced the bugs this
    /// file now guards against.
    /// </para>
    /// Repeated, because whether the two transactions actually interleave is a matter of timing; one pass could
    /// pass by luck.
    /// </remarks>
    [Fact]
    public async Task Concurrent_activations_of_different_versions_do_not_throw_and_leave_exactly_one_active()
    {
        var ct = CancellationToken.None;
        var v1 = ValidV1;
        var v2 = ValidV2;
        var v3 = ValidV2.Replace("version: 2", "version: 3");

        for (var attempt = 0; attempt < 12; attempt++)
        {
            await ResetProcessDefinitionsAsync();

            // All three versions stored, with version 2 active — the starting shape the race needs.
            await ActivateAsync(v1, ct);
            await ActivateAsync(v3, ct);
            await ActivateAsync(v2, ct);
            (await ActiveVersion("manufacture")).Should().Be(2);

            // One activates backwards to 1, the other forwards to 3, concurrently.
            var back = Task.Run(async () => await ActivateAsync(v1, ct), ct);
            var forward = Task.Run(async () => await ActivateAsync(v3, ct), ct);

            var both = async () => await Task.WhenAll(back, forward);
            await both.Should().NotThrowAsync(
                "a concurrent activation must resolve as a Result, not as a raw PostgresException — ProcessDefinitionSync has no catch for one, so it would escape the sync entirely. Removing LockProcessAsync is what breaks this, with 40P01");

            var active = await ActiveVersion("manufacture");
            active.Should().BeOneOf([1, 3], "whichever writer committed last decides, but it must be one of the two that ran");
            (await CountActiveAsync("manufacture")).Should().Be(1, "the one-active-version-per-process invariant must hold after the race, not just before it");
        }
    }

    /// <summary>Stores and activates <paramref name="yaml"/> through a store of its own, so concurrent callers do not share a connection.</summary>
    private async Task ActivateAsync(string yaml, CancellationToken ct)
    {
        var definition = ProcessLoader.Load(yaml);
        definition.IsSuccess.Should().BeTrue(definition.IsFailure ? definition.Error : "");
        var store = new OrmProcessDefinitionStore(new WorkflowOrmOptions { ConnectionString = pg.ConnectionString });
        (await store.UpsertAndActivateAsync(definition.Value, yaml, ct)).IsSuccess.Should().BeTrue();
    }

    private async Task ResetProcessDefinitionsAsync()
    {
        await using var connection = new NpgsqlConnection(pg.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM process_definition", connection);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CountActiveAsync(string process)
    {
        await using var connection = new NpgsqlConnection(pg.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM process_definition WHERE process = @p AND is_active", connection);
        cmd.Parameters.AddWithValue("p", process);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    // --- helpers -----------------------------------------------------------------------------------------

    /// <summary>The exact YAML stored for a version, for asserting a refused write changed nothing.</summary>
    private async Task<string?> StoredYaml(string process, int version)
    {
        await using var connection = new NpgsqlConnection(pg.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT yaml FROM process_definition WHERE process = @p AND version = @v", connection);
        cmd.Parameters.AddWithValue("p", process);
        cmd.Parameters.AddWithValue("v", version);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private Task WriteProcessFile(string yaml)
    {
        _source.Write(yaml);
        return Task.CompletedTask;
    }

    private Task<int?> ActiveVersion(string process) => _definitions.GetActiveVersionAsync(process, CancellationToken.None).AsTask();

    /// <summary>
    /// A single-document <see cref="IProcessDefinitionSource"/> test double: <see cref="Write"/> replaces the one
    /// document it holds, the same way rewriting a process file in a git checkout replaces what
    /// <see cref="ReadAllAsync"/> would read next. The real implementation is git-backed and lives with a host
    /// (Daedalus, a later task) — Thalos.NET never reaches for a filesystem itself, including here.
    /// </summary>
    private sealed class FakeProcessDefinitionSource : IProcessDefinitionSource
    {
        private string? _yaml;

        public void Write(string yaml) => _yaml = yaml;

        public ValueTask<IReadOnlyList<ProcessDocument>> ReadAllAsync(CancellationToken ct)
        {
            IReadOnlyList<ProcessDocument> documents = _yaml is null
                ? []
                : [new ProcessDocument("manufacture.process.yaml", _yaml)];
            return ValueTask.FromResult(documents);
        }
    }

    /// <summary>
    /// An <see cref="IWorkflowReferenceResolver"/> that resolves every agent and skill name — these tests are
    /// about activation, pinning and retention, not reference resolution (see
    /// <c>Thalos.Tests.Workflow.WorkflowReferenceResolverTests</c> for that), so the fixtures' agent and skill
    /// names never need to correspond to anything real.
    /// </summary>
    private sealed class AlwaysResolves : IWorkflowReferenceResolver
    {
        public ValueTask<AgentId?> ResolveAgentIdAsync(string name, CancellationToken ct) => ValueTask.FromResult<AgentId?>(AgentId.New());

        public ValueTask<bool> SkillExistsAsync(string name, CancellationToken ct) => ValueTask.FromResult(true);
    }
}
