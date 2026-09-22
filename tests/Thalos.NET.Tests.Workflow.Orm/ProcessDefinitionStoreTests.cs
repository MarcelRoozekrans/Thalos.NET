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
        var runId = await _store.StartAsync("manufacture", version: 1, "c1", "implement", ct);

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
        var runId = await _store.StartAsync("manufacture", 1, "c1", "implement", ct);
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
        var v1RunId = await _store.StartAsync("manufacture", 1, "c-v1", "implement", ct);
        await _store.StartAsync("manufacture", 2, "c-v2", "implement", ct); // stays Running for the whole test

        await _store.CancelAsync(v1RunId, "test", ct);

        (await _definitions.TryRemoveAsync("manufacture", version: 1, ct)).IsSuccess.Should().BeTrue(
            "version 1's own run is terminal; version 2's still-running run must not block removing version 1");
        (await _definitions.TryRemoveAsync("manufacture", version: 2, ct)).IsFailure.Should().BeTrue(
            "version 2's run is still running and must still block removing version 2");
    }

    // --- helpers -----------------------------------------------------------------------------------------

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
