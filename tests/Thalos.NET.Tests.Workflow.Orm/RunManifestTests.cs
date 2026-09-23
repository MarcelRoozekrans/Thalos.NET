using Thalos.Workflow;
using Thalos.Workflow.Orm;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>
/// <see cref="RunManifest"/> is written once by <see cref="OrmWorkflowStore.StartAsync(WorkflowStartRequest,CancellationToken)"/>
/// and never updated: this suite proves it round-trips at full length through the <c>jsonb</c> column — no
/// truncation, unlike <see cref="WorkflowRun.Variables"/> rendered into a node's task text — and that no later
/// write (<see cref="OrmWorkflowStore.CompleteNodeAsync"/> included) ever touches it.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Docker")]
public sealed class RunManifestTests(PostgresFixture pg) : IAsyncLifetime
{
    private OrmWorkflowStore _store = null!;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        _store = NewStore();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Red if <c>SelectRunSql</c> does not select <c>manifest</c>: <c>FindAsync</c> would then read every run's
    /// manifest back as <see langword="null"/> regardless of what <c>StartAsync</c> wrote, and this assertion
    /// would fail on the very first line. Also red if the <c>manifest</c> column, or the value written into it,
    /// truncates <see cref="RunManifest.Documents"/> anywhere short of its actual length — the whole reason
    /// documents exist is that <see cref="WorkflowRun.Variables"/> is cut at 512 characters when rendered into a
    /// node's task text, so a manifest that silently re-imposed the same cut would defeat its own purpose.
    /// </summary>
    [Fact]
    public async Task The_manifest_round_trips_with_documents_at_full_length()
    {
        var longDoc = new string('x', 5000);
        var manifest = new RunManifest
        {
            Nodes = new Dictionary<string, NodePin>(StringComparer.Ordinal)
            {
                ["implement"] = new NodePin("implementer", AgentId.New(), "rev-1", "manufacture-implement", "hash-1"),
            },
            Documents = new Dictionary<string, string>(StringComparer.Ordinal) { ["standing_instructions"] = longDoc },
        };

        var id = await _store.StartAsync(
            new WorkflowStartRequest { Process = "p", Version = 1, CorrelationKey = $"k:{Guid.NewGuid()}", StartNode = "implement", Manifest = manifest },
            CancellationToken.None);

        var run = await _store.FindAsync(id, CancellationToken.None);

        run.Should().NotBeNull();
        run!.Manifest.Should().BeEquivalentTo(manifest);
        run.Manifest!.Documents["standing_instructions"].Should().HaveLength(5000, "documents exist because variables are cut at 512");
    }

    /// <summary>
    /// Red if any <c>UPDATE</c> in <see cref="OrmWorkflowStore.CompleteNodeAsync"/> lists <c>manifest</c> among
    /// the columns it sets — deliberately introduced and reverted to watch this assertion fail; see the task
    /// report for the exact diff and failure. A run's pin must survive every node it completes untouched, because
    /// nothing downstream of <c>StartAsync</c> is entitled to repin an in-flight run to a different agent or
    /// skill build.
    /// </summary>
    [Fact]
    public async Task Completing_nodes_never_changes_the_manifest()
    {
        var manifest = new RunManifest
        {
            Nodes = new Dictionary<string, NodePin>(StringComparer.Ordinal) { ["a"] = new NodePin("x", AgentId.New(), "r", "s", "h") },
        };

        var id = await _store.StartAsync(
            new WorkflowStartRequest { Process = "p", Version = 1, CorrelationKey = $"k:{Guid.NewGuid()}", StartNode = "a", Manifest = manifest },
            CancellationToken.None);

        var run = await _store.FindAsync(id, CancellationToken.None);
        await _store.CompleteNodeAsync(
            id, run!.CurrentSeq,
            new WorkflowTransition("b", WorkflowStatus.Running, null, WorkflowEventKind.Completed),
            new NodeResult("ok", new Dictionary<string, object?>(StringComparer.Ordinal) { ["k"] = "v" }),
            CancellationToken.None);

        (await _store.FindAsync(id, CancellationToken.None))!.Manifest.Should().BeEquivalentTo(manifest);
    }

    /// <summary>
    /// Red if the legacy positional overload synthesises anything other than a <see langword="null"/> manifest —
    /// deliberately introduced and reverted to watch this assertion fail; see the task report. A caller on the
    /// pre-0.10.0 API surface, or one that genuinely wants no pin, must get a run indistinguishable from one
    /// started before manifests existed.
    /// </summary>
    [Fact]
    public async Task The_legacy_overload_starts_a_run_with_no_manifest()
    {
        var id = await _store.StartAsync("p", 1, $"k:{Guid.NewGuid()}", "implement", initialVariables: null, CancellationToken.None);

        (await _store.FindAsync(id, CancellationToken.None))!.Manifest.Should().BeNull();
    }

    /// <summary>
    /// A run started with no manifest at all — the request's <see cref="WorkflowStartRequest.Manifest"/> left
    /// <see langword="null"/> — must read back <see langword="null"/> too, the same as the legacy overload.
    /// Distinct from the legacy-overload test above: this one goes through the request-based overload directly,
    /// so a bug that only affects the request path and not the positional-to-request forwarding would be missed
    /// by that test alone.
    /// </summary>
    [Fact]
    public async Task A_request_with_no_manifest_starts_a_run_with_no_manifest()
    {
        var id = await _store.StartAsync(
            new WorkflowStartRequest { Process = "p", Version = 1, CorrelationKey = $"k:{Guid.NewGuid()}", StartNode = "implement" },
            CancellationToken.None);

        (await _store.FindAsync(id, CancellationToken.None))!.Manifest.Should().BeNull();
    }

    /// <summary>
    /// The idempotent path — <see cref="WorkflowStartRequest.CorrelationKey"/> already in use — starts nothing,
    /// so it must not silently repin the existing run to the second call's manifest either. Mirrors
    /// <c>OrmWorkflowStoreTests.StartAsync_on_an_existing_correlation_key_leaves_that_runs_variables_alone</c> for
    /// <see cref="WorkflowRun.Manifest"/> instead of <see cref="WorkflowRun.Variables"/>.
    /// </summary>
    [Fact]
    public async Task Starting_on_an_existing_correlation_key_leaves_that_runs_manifest_alone()
    {
        var key = $"k:{Guid.NewGuid()}";
        var firstManifest = new RunManifest
        {
            Nodes = new Dictionary<string, NodePin>(StringComparer.Ordinal) { ["a"] = new NodePin("first", AgentId.New(), "r1", "s1", "h1") },
        };
        var secondManifest = new RunManifest
        {
            Nodes = new Dictionary<string, NodePin>(StringComparer.Ordinal) { ["a"] = new NodePin("second", AgentId.New(), "r2", "s2", "h2") },
        };

        var first = await _store.StartAsync(
            new WorkflowStartRequest { Process = "p", Version = 1, CorrelationKey = key, StartNode = "a", Manifest = firstManifest },
            CancellationToken.None);
        var second = await _store.StartAsync(
            new WorkflowStartRequest { Process = "p", Version = 1, CorrelationKey = key, StartNode = "a", Manifest = secondManifest },
            CancellationToken.None);

        second.Should().Be(first);
        (await _store.FindAsync(first, CancellationToken.None))!.Manifest.Should().BeEquivalentTo(firstManifest);
    }

    private OrmWorkflowStore NewStore() =>
        new(new WorkflowOrmOptions { ConnectionString = pg.ConnectionString }, NewDefinitionStore());

    private OrmProcessDefinitionStore NewDefinitionStore() =>
        new(new WorkflowOrmOptions { ConnectionString = pg.ConnectionString });
}
