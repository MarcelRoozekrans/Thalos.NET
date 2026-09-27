using Microsoft.Extensions.DependencyInjection;
using Thalos.Workflow;
using Thalos.Workflow.Orm;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>
/// Task A19: <see cref="OrmWorkflowStore.CompleteNodeAsync"/> records the completed node's
/// <see cref="NodeResult.Usage"/> on the event it appends, <see cref="IWorkflowRunHistory.ListEventsAsync"/> reads
/// every event back with every field intact, events that ran no agent turn carry no usage, and
/// <c>AddWorkflowOrm</c> puts one store behind both interfaces.
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

    private static readonly RunPrincipal Approver = new("approver-1", ["admin"]);

    /// <summary>A start node straight into an approval gate, for the resume test.</summary>
    private const string GateYaml = """
        process: node-usage
        version: 1
        nodes:
          work: { next: gate }
          gate: { await: go, next: done }
          done: { terminal: succeeded }
        """;

    /// <summary>How far the database clock may sit from the test's when checking <see cref="WorkflowRunEvent.CreatedAt"/>.</summary>
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(1);

    private OrmWorkflowStore _store = null!;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        var options = new WorkflowOrmOptions { ConnectionString = pg.ConnectionString };
        var definitions = new OrmProcessDefinitionStore(options);
        var definition = ProcessLoader.Load(GateYaml);
        definition.IsSuccess.Should().BeTrue(definition.IsFailure ? definition.Error : "");
        await definitions.UpsertAndActivateAsync(definition.Value, GateYaml, CancellationToken.None);
        _store = new OrmWorkflowStore(options, definitions);
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
        completion.Should().BeEquivalentTo(new
        {
            Seq = 1L,
            Kind = nameof(WorkflowEventKind.Completed),
            FromNode = "work",
            ToNode = "done",
            Status = nameof(WorkflowStatus.Running),
            Outcome = "ok",
            Error = (string?)null,
        });
        completion.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, ClockTolerance);
        completion.Usage.Should().NotBeNull();
        // Member by member, not by record equality, so a mismatch names the field that differs.
        completion.Usage!.Value.Should().BeEquivalentTo(NodeUsage, options => options.ComparingByMembers<TurnUsage>());
    }

    [Fact]
    public async Task The_seeded_entered_event_has_no_usage()
    {
        var runId = await StartAsync();
        // A second run's log in the same table, so a read that ignored the run id would return two events.
        await StartAsync();

        var events = await _store.ListEventsAsync(runId, CancellationToken.None);

        var entered = events.Should().ContainSingle().Subject;
        entered.Should().BeEquivalentTo(new
        {
            Seq = 0L,
            Kind = nameof(WorkflowEventKind.Entered),
            FromNode = (string?)null,
            ToNode = "work",
            Status = nameof(WorkflowStatus.Running),
            Outcome = (string?)null,
            Error = (string?)null,
        });
        entered.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, ClockTolerance);
        entered.Usage.Should().BeNull();
    }

    [Fact]
    public async Task A_resume_event_has_no_usage()
    {
        var runId = await StartAsync();
        await _store.CompleteNodeAsync(
            runId, seq: 1,
            new WorkflowTransition("gate", WorkflowStatus.Awaiting, "go", WorkflowEventKind.Awaiting),
            new NodeResult(null, Empty) { Usage = NodeUsage }, CancellationToken.None);
        var resumed = await _store.ResumeAsync(runId, new WorkflowResumeRequest { Signal = "go", ResumedBy = Approver }, CancellationToken.None);
        resumed.IsSuccess.Should().BeTrue(resumed.IsFailure ? resumed.Error : "");

        var events = await _store.ListEventsAsync(runId, CancellationToken.None);

        events.Select(e => e.Kind).Should().Equal(
            nameof(WorkflowEventKind.Entered), nameof(WorkflowEventKind.Awaiting), nameof(WorkflowEventKind.Resumed));
        var resume = events[2];
        resume.Error.Should().BeNull();
        resume.Usage.Should().BeNull();
    }

    [Theory]
    [InlineData(nameof(WorkflowEventKind.Failed))]
    [InlineData("Cancelled")]
    public async Task A_failure_or_cancellation_event_records_its_reason_and_no_usage(string kind)
    {
        var runId = await StartAsync();
        var reason = $"{kind} for the test";
        if (string.Equals(kind, nameof(WorkflowEventKind.Failed), StringComparison.Ordinal))
        {
            await _store.FailAsync(runId, reason, CancellationToken.None);
        }
        else
        {
            await _store.CancelAsync(runId, reason, CancellationToken.None);
        }

        var events = await _store.ListEventsAsync(runId, CancellationToken.None);

        events.Should().HaveCount(2);
        var ended = events[1];
        ended.Kind.Should().Be(kind);
        ended.Error.Should().Be(reason);
        ended.Usage.Should().BeNull();
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
