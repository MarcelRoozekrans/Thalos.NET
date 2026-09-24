using Thalos.Skills;
using Thalos.Workflow;
using Thalos.Workflow.Orm;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>
/// The property Task 6 established the <c>process_definition</c> table for but nothing yet depended on: a
/// definition that has been synced is <em>reachable by a run</em>. Task 6 could activate a version, and Task 6's
/// own suite proved the row landed and the retention rule held — but both places that needed a run's
/// <see cref="ProcessDefinition"/> read it from a dictionary a host populated separately, so syncing a file and
/// making it runnable were two unrelated acts. These tests fail if resolution ever stops going through
/// <see cref="IProcessDefinitionStore"/>, because the only thing that ever puts a definition anywhere here is
/// <see cref="ProcessDefinitionSync"/> writing to the table.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Docker")]
public sealed class SyncedDefinitionReachabilityTests(PostgresFixture pg) : IAsyncLifetime
{
    /// <summary>A two-node sequence: one task node run by an agent, then a terminal.</summary>
    private const string PipelineV1 = """
        process: pipeline
        version: 1
        nodes:
          implement: { agent: backend, skill: tdd, next: done }
          done: { terminal: succeeded }
        """;

    /// <summary>
    /// Version 2 of the same process, routing <c>implement</c> somewhere version 1 has no node for. A run pinned
    /// to version 1 that accidentally resolved against version 2 would land on <c>audit</c>, so "which version
    /// did resolution actually use" is answerable from the run's own next node rather than inferred.
    /// </summary>
    private const string PipelineV2 = """
        process: pipeline
        version: 2
        nodes:
          implement: { agent: backend, skill: tdd, next: audit }
          audit: { agent: auditor, skill: review, next: done }
          done: { terminal: succeeded }
        """;

    private OrmProcessDefinitionStore _definitions = null!;
    private OrmWorkflowStore _store = null!;
    private FakeSource _source = null!;
    private ProcessDefinitionSync _sync = null!;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        var options = new WorkflowOrmOptions { ConnectionString = pg.ConnectionString };
        _definitions = new OrmProcessDefinitionStore(options);
        _store = new OrmWorkflowStore(options, _definitions);
        _source = new FakeSource();
        _sync = new ProcessDefinitionSync(_source, _definitions, new AlwaysResolves());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The headline property. Nothing registers <c>pipeline</c> anywhere in memory: the only reason the dispatcher
    /// can run its node is that <see cref="ProcessDefinitionSync"/> put the YAML in <c>process_definition</c> and
    /// resolution reads it back from there. Turns red if <see cref="WorkflowNodeDispatcher"/> stops resolving
    /// through <see cref="IProcessDefinitionStore"/> — which is exactly the state the code was in before this
    /// change, when a synced definition was not runnable without a second, separate registration.
    /// </summary>
    [Fact]
    public async Task A_definition_synced_from_a_source_is_reachable_by_a_run()
    {
        _source.Write(PipelineV1);
        var synced = await _sync.SyncAsync(CancellationToken.None);
        synced.IsSuccess.Should().BeTrue(synced.IsFailure ? synced.Error : "");

        var runId = await _store.StartAsync("pipeline", 1, "c-reach", "implement", initialVariables: null, CancellationToken.None);

        // No message is constructed here: StartAsync enqueued 'implement''s own dispatch, and the drain takes it
        // off the table exactly as a host's outbox consumer would.
        (await OutboxDrain.DispatchNextAsync(pg.ConnectionString, NewDispatcher())).Should().BeTrue("StartAsync must leave a dispatch for the start node in the outbox");

        var run = await _store.FindAsync(runId, CancellationToken.None);
        run!.Status.Should().NotBe(
            WorkflowStatus.Failed,
            "a definition that has been synced must be runnable — a failed run here means resolution never read the table the sync wrote to. Error: {0}",
            run.LastError ?? "<none>");
        run.CurrentNode.Should().Be("done", "the synced definition's 'implement' node routes to 'done', so reaching it proves the graph came from the stored YAML");
    }

    /// <summary>
    /// The pin, now that resolution is live rather than a host-held dictionary. Activating version 2 changes which
    /// version <em>new</em> runs start on and nothing else: a run that started on version 1 keeps resolving
    /// version 1's graph. Turns red if resolution ever switches to <see cref="IProcessDefinitionStore.GetActiveVersionAsync"/>,
    /// or if the read path grows an <c>is_active</c> filter — either would send this run to version 2's
    /// <c>audit</c> node instead of version 1's <c>done</c>.
    /// </summary>
    [Fact]
    public async Task A_run_keeps_resolving_its_pinned_version_after_a_newer_one_activates()
    {
        _source.Write(PipelineV1);
        (await _sync.SyncAsync(CancellationToken.None)).IsSuccess.Should().BeTrue();

        var runId = await _store.StartAsync("pipeline", 1, "c-pinned", "implement", initialVariables: null, CancellationToken.None);

        // The hot reload a live run must survive untouched.
        _source.Write(PipelineV2);
        (await _sync.SyncAsync(CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await _definitions.GetActiveVersionAsync("pipeline", CancellationToken.None)).Should().Be(2, "the newer version is what new runs would start on");

        (await OutboxDrain.DispatchNextAsync(pg.ConnectionString, NewDispatcher())).Should().BeTrue("StartAsync must leave a dispatch for the start node in the outbox");

        var run = await _store.FindAsync(runId, CancellationToken.None);
        run!.CurrentNode.Should().Be("done", "version 1's 'implement' routes to 'done'; landing on 'audit' would mean the run was silently moved onto version 2's graph");
        run.ProcessVersion.Should().Be(1);
    }

    /// <summary>
    /// A run pinned to a version the table does not hold. The dispatcher must fail the run with a message naming
    /// the process and version — never throw, because a throw hands the message back to the outbox for eight
    /// retries, each one re-running a paid agent turn against a configuration problem no retry can fix. Turns red
    /// if the unresolved case is ever allowed to propagate, or if it silently returns without recording anything.
    /// </summary>
    [Fact]
    public async Task A_run_whose_pinned_version_is_not_stored_fails_the_run_instead_of_throwing()
    {
        // Started without ever syncing anything: StartAsync stamps the pin independently of this store, so a run
        // can legitimately exist pointing at a version no row backs.
        var runId = await _store.StartAsync("pipeline", 7, "c-missing", "implement", initialVariables: null, CancellationToken.None);
        var dispatcher = NewDispatcher();

        var dispatch = async () => await OutboxDrain.DispatchNextAsync(pg.ConnectionString, dispatcher);

        await dispatch.Should().NotThrowAsync("an unresolvable definition is a node failure, not an infrastructure failure the outbox should retry");

        var run = await _store.FindAsync(runId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Failed, "the run must be recorded as failed, not left running for a sweep to find");
        run.LastError.Should().Contain("pipeline").And.Contain("7", "the error has to name the process and version a human needs to go fix");
    }

    /// <summary>
    /// The resume half of resolution. <c>ResumeAsync</c> resolved from the same host dictionary the dispatcher did,
    /// so this covers the second of the two call sites: a gate parked by a dispatch must come back out against the
    /// definition the table holds. Turns red if <c>ResumeAsync</c> stops consulting <see cref="IProcessDefinitionStore"/>.
    /// </summary>
    [Fact]
    public async Task A_gate_resumes_against_the_definition_the_table_holds()
    {
        const string gated = """
            process: gated
            version: 1
            nodes:
              start: { agent: backend, skill: tdd, next: gate }
              gate: { await: human_approval, next: done }
              done: { terminal: succeeded }
            """;

        _source.Write(gated);
        (await _sync.SyncAsync(CancellationToken.None)).IsSuccess.Should().BeTrue();

        var runId = await _store.StartAsync("gated", 1, "c-gate-resume", "start", initialVariables: null, CancellationToken.None);

        // Two dispatches park the gate: the first completes 'start' and moves the run to 'gate'; the second,
        // enqueued by that very transition, is what Advance parks at Awaiting. Both come off the outbox — the
        // drain stops on its own once the gate is parked, because a parked run enqueues nothing further.
        var dispatched = await OutboxDrain.DrainAsync(pg.ConnectionString, NewDispatcher());
        dispatched.Should().Be(2, "StartAsync enqueues 'start', and completing 'start' enqueues 'gate'");

        var parked = await _store.FindAsync(runId, CancellationToken.None);
        parked!.Status.Should().Be(WorkflowStatus.Awaiting, "the run must be parked before resume is meaningful. Error: {0}", parked.LastError ?? "<none>");

        var resumed = await _store.ResumeAsync(runId, "human_approval", "approved", CancellationToken.None);

        resumed.IsSuccess.Should().BeTrue(resumed.IsFailure ? resumed.Error : "");
        (await _store.FindAsync(runId, CancellationToken.None))!.CurrentNode.Should().Be("done");
    }

    /// <summary>
    /// A dispatcher over the same definition store the sync writes to, with the agent turn faked — dispatching a
    /// real subagent is out of scope here, and the node under test declares no outcomes, so an empty turn is a
    /// well-formed result rather than a shortcut.
    /// </summary>
    private WorkflowNodeDispatcher NewDispatcher() =>
        // No run started by these tests carries a manifest, so this ISkillStore is never actually read from —
        // an empty InMemorySkillStore stands in purely to satisfy the constructor.
        new(_store, new AlwaysSucceedsRunner(), new AlwaysResolves(), _definitions, new InMemorySkillStore(TimeProvider.System), _ => new StubSecurityContext());

    private sealed class FakeSource : IProcessDefinitionSource
    {
        private string? _yaml;

        public void Write(string yaml) => _yaml = yaml;

        public ValueTask<IReadOnlyList<ProcessDocument>> ReadAllAsync(CancellationToken ct)
        {
            IReadOnlyList<ProcessDocument> documents = _yaml is null ? [] : [new ProcessDocument("pipeline.process.yaml", _yaml)];
            return ValueTask.FromResult(documents);
        }
    }

    /// <summary>Resolves every agent and skill name: these tests are about definition resolution, not reference resolution.</summary>
    private sealed class AlwaysResolves : IWorkflowReferenceResolver
    {
        public ValueTask<AgentId?> ResolveAgentIdAsync(string name, CancellationToken ct) => ValueTask.FromResult<AgentId?>(AgentId.New());

        public ValueTask<bool> SkillExistsAsync(string name, CancellationToken ct) => ValueTask.FromResult(true);
    }

    /// <summary>A turn that completes cleanly and reports no outcome — valid for a node with no declared outcomes.</summary>
    private sealed class AlwaysSucceedsRunner : ISubagentRunner
    {
        public ValueTask<Result<AgentTurnResult, AgentError>> RunAsync(SubagentRunRequest request, CancellationToken ct = default) =>
            ValueTask.FromResult(Result<AgentTurnResult, AgentError>.Success(
                new AgentTurnResult(TurnId.New(), SessionId.New(), "done", TurnUsage.Empty("test-model"), [], TimeSpan.Zero)));
    }

    private sealed class StubSecurityContext : ISecurityContext
    {
        public string Id => "workflow-engine";
        public IReadOnlySet<string> Roles { get; } = new HashSet<string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, string> Claims { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
