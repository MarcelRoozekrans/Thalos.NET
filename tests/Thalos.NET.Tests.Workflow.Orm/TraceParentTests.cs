using System.Diagnostics;
using Thalos.Workflow;
using Thalos.Workflow.Orm;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>
/// 0.15.0: a run records the W3C traceparent that was current when it started, so each later dispatch can link its
/// span back to the request that started the run. Written once, by the INSERT, like <c>started_by</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Docker")]
public sealed class TraceParentTests(PostgresFixture pg) : IAsyncLifetime
{
    private OrmWorkflowStore _store = null!;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        _store = new OrmWorkflowStore(new WorkflowOrmOptions { ConnectionString = pg.ConnectionString }, new OrmProcessDefinitionStore(new WorkflowOrmOptions { ConnectionString = pg.ConnectionString }));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Red: leave <c>trace_parent</c> out of the INSERT; the run reads back null.
    /// Red: leave it out of <c>SelectRunSql</c> or <c>ReadRow</c>; the run reads back null.
    /// </summary>
    [Fact]
    public async Task A_run_started_under_an_activity_reads_back_its_traceparent()
    {
        string expected;
        Guid runId;
        using (var origin = new Activity("probe-start").SetIdFormat(ActivityIdFormat.W3C).Start())
        {
            expected = origin.Id!;
            runId = (await _store.StartAsync(Request("c-trace"), CancellationToken.None)).Value;
        }

        var run = await _store.FindAsync(runId, CancellationToken.None);

        run!.TraceParent.Should().Be(expected);
    }

    /// <summary>
    /// The precondition pins that no ambient activity leaks into this test. Red: write <c>Guid.NewGuid().ToString()</c>
    /// when there is no current activity; the null assertion fails.
    /// </summary>
    [Fact]
    public async Task A_run_started_without_an_activity_has_no_traceparent()
    {
        Activity.Current.Should().BeNull();

        var runId = (await _store.StartAsync(Request("c-no-trace"), CancellationToken.None)).Value;

        (await _store.FindAsync(runId, CancellationToken.None))!.TraceParent.Should().BeNull();
    }

    /// <summary>
    /// The idempotent path starts nothing, so it keeps the first start's traceparent. Red: add
    /// <c>DO UPDATE SET trace_parent = EXCLUDED.trace_parent</c> to the INSERT's conflict clause; the conflict then returns the new row's id, so the second start's
    /// <c>Value.Should().Be(runId)</c> assertion fails first.
    /// </summary>
    [Fact]
    public async Task A_repeated_start_keeps_the_first_traceparent()
    {
        string first;
        Guid runId;
        using (var origin = new Activity("probe-first").SetIdFormat(ActivityIdFormat.W3C).Start())
        {
            first = origin.Id!;
            runId = (await _store.StartAsync(Request("c-repeat"), CancellationToken.None)).Value;
        }

        using (new Activity("probe-second").SetIdFormat(ActivityIdFormat.W3C).Start())
        {
            (await _store.StartAsync(Request("c-repeat"), CancellationToken.None)).Value.Should().Be(runId);
        }

        (await _store.FindAsync(runId, CancellationToken.None))!.TraceParent.Should().Be(first);
    }

    private static WorkflowStartRequest Request(string correlationKey) => new()
    {
        Process = "trace-parent",
        Version = 1,
        CorrelationKey = correlationKey,
        StartNode = "implement",
        InitialVariables = null,
        StartedBy = TestPrincipals.Starter,
    };
}
