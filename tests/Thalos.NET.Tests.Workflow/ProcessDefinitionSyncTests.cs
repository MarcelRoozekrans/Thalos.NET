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
        var store = new FakeStore();
        var sync = new ProcessDefinitionSync(new FakeSource([new ProcessDocument("a.yaml", ManufactureV1)]), store, new AlwaysResolves());

        var result = await sync.SyncAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
        result.Value.Should().Be(1);
        store.Activated.Should().ContainSingle(d => d.Name == "manufacture" && d.Version == 1);
    }

    [Fact]
    public async Task SyncAsync_rejects_a_batch_with_two_documents_naming_the_same_process()
    {
        var store = new FakeStore();
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
        var store = new FakeStore();
        var other = ManufactureV1.Replace("process: manufacture", "process: other");
        var sync = new ProcessDefinitionSync(
            new FakeSource([new ProcessDocument("a.yaml", ManufactureV1), new ProcessDocument("b.yaml", other)]),
            store,
            new AlwaysResolves());

        var result = await sync.SyncAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
        result.Value.Should().Be(2);
    }

    private sealed class FakeSource(IReadOnlyList<ProcessDocument> documents) : IProcessDefinitionSource
    {
        public ValueTask<IReadOnlyList<ProcessDocument>> ReadAllAsync(CancellationToken ct) => ValueTask.FromResult(documents);
    }

    private sealed class FakeStore : IProcessDefinitionStore
    {
        public List<ProcessDefinition> Activated { get; } = [];

        public ValueTask UpsertAndActivateAsync(ProcessDefinition definition, string yaml, CancellationToken ct)
        {
            Activated.Add(definition);
            return ValueTask.CompletedTask;
        }

        public ValueTask<int?> GetActiveVersionAsync(string process, CancellationToken ct) =>
            ValueTask.FromResult<int?>(Activated.Where(d => string.Equals(d.Name, process, StringComparison.Ordinal)).Select(d => (int?)d.Version).LastOrDefault());

        public ValueTask<Result> TryRemoveAsync(string process, int version, CancellationToken ct) =>
            ValueTask.FromResult(Result.Success());
    }

    private sealed class AlwaysResolves : IWorkflowReferenceResolver
    {
        public ValueTask<AgentId?> ResolveAgentIdAsync(string name, CancellationToken ct) => ValueTask.FromResult<AgentId?>(AgentId.New());

        public ValueTask<bool> SkillExistsAsync(string name, CancellationToken ct) => ValueTask.FromResult(true);
    }
}
