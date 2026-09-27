using Thalos.Skills;
using Thalos.Workflow;
using ZeroAlloc.Results;

namespace Thalos.Tests.Workflow;

/// <summary>
/// Tests for <see cref="ProcessDefinitionSync"/> against fakes: no Postgres involved, since the duplicate-name
/// guard and the activate/skip decision are pure logic over <see cref="IProcessDefinitionSource"/> and
/// <see cref="IProcessDefinitionStore"/>. Property and pinning tests against a real store live in
/// <c>Thalos.Tests.Workflow.Orm.ProcessDefinitionStoreTests</c>.
/// </summary>
public sealed class ProcessDefinitionSyncTests
{
    private const string ManufactureV1 = """
        process: manufacture
        version: 1
        nodes:
          implement: { agent: backend, skill: tdd, next: done }
          done: { terminal: succeeded }
        """;

    private const string ManufactureV2 = """
        process: manufacture
        version: 2
        nodes:
          implement: { agent: backend, skill: tdd, next: done }
          done: { terminal: succeeded }
        """;

    [Fact]
    public async Task SyncAsync_activates_a_single_valid_document()
    {
        var store = new InMemoryProcessDefinitionStore();
        var sync = new ProcessDefinitionSync(new FakeSource([new ProcessDocument("a.yaml", ManufactureV1)]), store, new AlwaysResolves());

        var result = await sync.SyncAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
        result.Value.Should().Be(1);
        store.Activated.Should().ContainSingle(d => d.Name == "manufacture" && d.Version == 1);
    }

    [Fact]
    public async Task SyncAsync_rejects_a_batch_with_two_documents_naming_the_same_process()
    {
        var store = new InMemoryProcessDefinitionStore();
        var sync = new ProcessDefinitionSync(
            new FakeSource([
                new ProcessDocument("manufacture.yaml", ManufactureV2),
                new ProcessDocument("manufacture-old.yaml", ManufactureV1),
            ]),
            store,
            new AlwaysResolves());

        var result = await sync.SyncAsync(CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("manufacture.yaml").And.Contain("manufacture-old.yaml").And.Contain("'manufacture'");
        store.Activated.Should().BeEmpty("a duplicate anywhere in the batch must block activating anything in it, not just the duplicated process — which document 'wins' must never depend on listing order");
    }

    [Fact]
    public async Task SyncAsync_does_not_treat_two_different_processes_as_duplicates()
    {
        var store = new InMemoryProcessDefinitionStore();
        var other = ManufactureV1.Replace("process: manufacture", "process: other");
        var sync = new ProcessDefinitionSync(
            new FakeSource([new ProcessDocument("a.yaml", ManufactureV1), new ProcessDocument("b.yaml", other)]),
            store,
            new AlwaysResolves());

        var result = await sync.SyncAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
        result.Value.Should().Be(2);
    }

    /// <summary>A process whose only non-terminal node is an action node, so no agent or skill needs resolving.</summary>
    private const string ActionProcessYaml = """
        process: publishing
        version: 1
        nodes:
          publish:
            action: open-pull-request
            outcomes: [published, failed]
            branch: { published: done, failed: stop }
          done: { terminal: succeeded }
          stop: { terminal: failed }
        """;

    [Fact]
    public async Task Sync_rejects_a_process_naming_an_unregistered_action()
    {
        var store = new InMemoryProcessDefinitionStore();
        var sync = SyncWith(ActionProcessYaml, store, new WorkflowReferenceResolver(Catalog(), Skills(), hostActions: []));

        var result = await sync.SyncAsync(CancellationToken.None);

        // Red if ValidateReferencesAsync drops the host-action rule: the process validates and is activated.
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("node 'publish' references unknown host action 'open-pull-request'");
    }

    [Fact]
    public async Task Sync_accepts_it_once_the_action_is_registered()
    {
        var store = new InMemoryProcessDefinitionStore();
        var action = RecordingAction.Returning("open-pull-request", new HostActionResult("published", new Dictionary<string, object?>(StringComparer.Ordinal)));
        var sync = SyncWith(ActionProcessYaml, store, new WorkflowReferenceResolver(Catalog(), Skills(), hostActions: [action]));

        var result = await sync.SyncAsync(CancellationToken.None);

        // Red if WorkflowReferenceResolver.HostActionExistsAsync returns false instead of looking the name up.
        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
        store.Activated.Should().ContainSingle(d => d.Name == "publishing" && d.Version == 1);
    }

    private static ProcessDefinitionSync SyncWith(string yaml, InMemoryProcessDefinitionStore store, IWorkflowReferenceResolver resolver) =>
        new(new FakeSource([new ProcessDocument("publishing.process.yaml", yaml)]), store, resolver);

    private static FakeAgentCatalog Catalog() => new([]);

    private static InMemorySkillStore Skills() => new(TimeProvider.System);

    private sealed class FakeSource(IReadOnlyList<ProcessDocument> documents) : IProcessDefinitionSource
    {
        public ValueTask<IReadOnlyList<ProcessDocument>> ReadAllAsync(CancellationToken ct) => ValueTask.FromResult(documents);
    }

    private sealed class AlwaysResolves : IWorkflowReferenceResolver
    {
        public ValueTask<AgentId?> ResolveAgentIdAsync(string name, CancellationToken ct) => ValueTask.FromResult<AgentId?>(AgentId.New());

        public ValueTask<bool> SkillExistsAsync(string name, CancellationToken ct) => ValueTask.FromResult(true);

        public ValueTask<bool> HostActionExistsAsync(string name, CancellationToken ct) => ValueTask.FromResult(true);
    }
}
