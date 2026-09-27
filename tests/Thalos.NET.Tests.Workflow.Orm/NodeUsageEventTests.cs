using Microsoft.Extensions.DependencyInjection;
using Thalos.Workflow;
using Thalos.Workflow.Orm;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>
/// Task A19: <see cref="OrmWorkflowStore.CompleteNodeAsync"/> records the completed node's
/// <see cref="NodeResult.Usage"/> on the event it appends, <see cref="IWorkflowRunHistory.ListEventsAsync"/> reads it
/// back with every field intact, and <c>AddWorkflowOrm</c> puts one store behind both interfaces.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Docker")]
public sealed class NodeUsageEventTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly IReadOnlyDictionary<string, object?> Empty = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>
    /// Every field distinct and non-zero, so a store that drops, swaps or zeroes any one of them — the cache counts,
    /// which are <c>init</c> properties outside the positional constructor, above all — reads back unequal.
    /// </summary>
    private static readonly TurnUsage NodeUsage = new(1200, 345, "claude-test-model") { CacheReadTokens = 800, CacheWriteTokens = 150 };

    private OrmWorkflowStore _store = null!;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        var options = new WorkflowOrmOptions { ConnectionString = pg.ConnectionString };
        _store = new OrmWorkflowStore(options, new OrmProcessDefinitionStore(options));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_completion_event_records_the_node_usage_it_was_given()
    {
        var runId = await StartAsync();

        await _store.CompleteNodeAsync(
            runId, seq: 1,
            new WorkflowTransition("done", WorkflowStatus.Running, null, WorkflowEventKind.Completed),
            new NodeResult("ok", Empty) { Usage = NodeUsage }, CancellationToken.None);

        var events = await _store.ListEventsAsync(runId, CancellationToken.None);

        events.Select(e => e.Seq).Should().Equal(0, 1);
        var completion = events[1];
        completion.Kind.Should().Be(nameof(WorkflowEventKind.Completed));
        completion.Usage.Should().Be(NodeUsage);
    }

    [Fact]
    public async Task The_seeded_entered_event_has_no_usage()
    {
        var runId = await StartAsync();
        // A second run's log in the same table, so a read that ignored the run id would return two events.
        await StartAsync();

        var events = await _store.ListEventsAsync(runId, CancellationToken.None);

        var entered = events.Should().ContainSingle().Subject;
        entered.Kind.Should().Be(nameof(WorkflowEventKind.Entered));
        entered.Usage.Should().BeNull();
    }

    [Fact]
    public void AddWorkflowOrm_resolves_the_history_and_the_store_to_the_same_instance()
    {
        var services = new ServiceCollection();
        services.AddThalos(thalos => thalos.AddWorkflowOrm(o =>
        {
            o.ConnectionString = pg.ConnectionString;
            o.EnsureSchemaOnStartup = false;
        }));
        using var provider = services.BuildServiceProvider();

        var history = provider.GetRequiredService<IWorkflowRunHistory>();
        var store = provider.GetRequiredService<IWorkflowStore>();

        history.Should().BeSameAs(store);
    }

    private async Task<Guid> StartAsync() =>
        (await _store.StartAsync(
            new WorkflowStartRequest
            {
                Process = "node-usage",
                Version = 1,
                CorrelationKey = $"c-node-usage-{Guid.NewGuid()}",
                StartNode = "work",
                InitialVariables = null,
                StartedBy = TestPrincipals.Starter,
            },
            CancellationToken.None)).Value;
}
