using Thalos.Workflow;
using Thalos.Workflow.Orm;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>
/// Task A3's own suite: proves <see cref="OrmWorkflowStore.ResumeAsync"/> records who resumed a run through a
/// gate and when, refuses a resume with no approver before anything is written (ruling R20), and records
/// nothing when a resume is refused for an unrelated reason (a signal mismatch).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Docker")]
public sealed class ResumedByTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly IReadOnlyDictionary<string, object?> Empty = new Dictionary<string, object?>(StringComparer.Ordinal);

    private static readonly RunPrincipal Approver = new("approver-1", ["admin"]) { DisplayName = "approver" };

    /// <summary>The YAML behind <see cref="StartParkedAtGateAsync"/>: a start node straight into an approval gate.</summary>
    private const string ApprovalYaml = """
        process: resumed-by-approval
        version: 1
        nodes:
          start: { next: gate }
          gate: { await: go, next: done }
          done: { terminal: succeeded }
        """;

    private OrmWorkflowStore _store = null!;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();

        var options = new WorkflowOrmOptions { ConnectionString = pg.ConnectionString };
        var definitions = new OrmProcessDefinitionStore(options);
        var definition = ProcessLoader.Load(ApprovalYaml);
        definition.IsSuccess.Should().BeTrue(definition.IsFailure ? definition.Error : "");
        await definitions.UpsertAndActivateAsync(definition.Value, ApprovalYaml, CancellationToken.None);

        _store = new OrmWorkflowStore(options, definitions);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_resume_records_who_resumed_and_when()
    {
        var id = await StartParkedAtGateAsync();
        var result = await _store.ResumeAsync(id, new WorkflowResumeRequest { Signal = "go", ResumedBy = Approver }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : "");
        var run = await _store.FindAsync(id, CancellationToken.None);
        run!.LastResume!.By.Should().BeEquivalentTo(Approver);
        run.LastResume.Signal.Should().Be("go");
        run.LastResume.At.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task A_resume_with_no_approver_is_refused_before_anything_is_written()
    {
        var id = await StartParkedAtGateAsync();
        var act = async () => await _store.ResumeAsync(id, new WorkflowResumeRequest { Signal = "go", ResumedBy = null! }, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
        var run = await _store.FindAsync(id, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Awaiting);
        run.LastResume.Should().BeNull();
    }

    [Fact]
    public async Task A_refused_resume_records_nothing()
    {
        var id = await StartParkedAtGateAsync();
        (await _store.ResumeAsync(id, new WorkflowResumeRequest { Signal = "wrong", ResumedBy = Approver }, CancellationToken.None)).IsFailure.Should().BeTrue();
        (await _store.FindAsync(id, CancellationToken.None))!.LastResume.Should().BeNull();
    }

    /// <summary>
    /// Starts a run and drives it straight to the parked gate, mirroring <c>OrmWorkflowStoreTests</c>' own
    /// gate arrangement for its resume tests.
    /// </summary>
    private async Task<Guid> StartParkedAtGateAsync()
    {
        var runId = (await _store.StartAsync(
            new WorkflowStartRequest
            {
                Process = "resumed-by-approval",
                Version = 1,
                CorrelationKey = $"c-resumed-by-{Guid.NewGuid()}",
                StartNode = "start",
                InitialVariables = null,
                StartedBy = TestPrincipals.Starter,
            },
            CancellationToken.None)).Value;

        await _store.CompleteNodeAsync(
            runId, seq: 1,
            new WorkflowTransition("gate", WorkflowStatus.Awaiting, "go", WorkflowEventKind.Awaiting),
            new NodeResult(null, Empty), CancellationToken.None);

        return runId;
    }
}
