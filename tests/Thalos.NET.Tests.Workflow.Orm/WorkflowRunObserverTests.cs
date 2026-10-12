using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Thalos.Workflow;
using Thalos.Workflow.Orm;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>
///     <see cref="OrmWorkflowStore"/> tells every <see cref="IWorkflowRunObserver"/> about each committed change,
///     after the commit, and about no refused change.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Docker")]
public sealed class WorkflowRunObserverTests(PostgresFixture pg) : IAsyncLifetime
{
    private readonly RecordingObserver _observer = new(pg.ConnectionString);
    private readonly ListLogger _log = new();
    private OrmWorkflowStore _store = null!;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        _store = NewStore(_observer);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    ///     Start notifies <c>Started</c> once, at the start node, and the observer can already read the run on its own
    ///     connection, so it ran after the commit.
    ///     Red: drop the NotifyAsync call after CommitAndRecordStartAsync.
    ///     Red: call NotifyAsync before the commit: the observer's own connection sees no row.
    /// </summary>
    [Fact]
    public async Task Start_notifies_after_the_commit()
    {
        var runId = await StartAsync("c1");

        var seen = _observer.Changes.Should().ContainSingle().Subject;
        seen.Change.Should().Be(new WorkflowRunChange(runId, WorkflowRunChangeKind.Started, WorkflowStatus.Running, "implement", null));
        seen.VisibleOnAnotherConnection.Should().BeTrue();
    }

    /// <summary>
    ///     Starting the same correlation key twice commits nothing the second time and notifies once.
    ///     Red: notify on the idempotent path.
    /// </summary>
    [Fact]
    public async Task The_idempotent_start_notifies_nothing()
    {
        await StartAsync("c1");
        await StartAsync("c1");

        _observer.Changes.Should().ContainSingle();
    }

    /// <summary>
    ///     Completing a node notifies <c>NodeCompleted</c> with the transition's node, status and signal.
    ///     Red: drop the NotifyAsync call in CompleteNodeAsync.
    ///     Red: pass the row's node instead of the transition's.
    /// </summary>
    [Fact]
    public async Task Completing_a_node_notifies_the_next_node()
    {
        var runId = await StartAsync("c1");
        _observer.Clear();

        await _store.CompleteNodeAsync(runId, seq: 1,
            new WorkflowTransition("review", WorkflowStatus.Running, null, WorkflowEventKind.Completed),
            new NodeResult("ok", new Dictionary<string, object?>(StringComparer.Ordinal)), CancellationToken.None);

        _observer.Changes.Select(c => c.Change).Should().Equal(
            new WorkflowRunChange(runId, WorkflowRunChangeKind.NodeCompleted, WorkflowStatus.Running, "review", null));
    }

    /// <summary>
    ///     A resume with the wrong signal is refused and notifies nothing; the right signal notifies <c>Resumed</c> with
    ///     the gate's transition: the approval gate's successor <c>done</c>, still Running, awaiting nothing.
    ///     Red: notify before the Result.Failure returns. Red: drop the NotifyAsync call after Resume's commit.
    ///     Red: pass row.CurrentNode and row.Status in ResumeAsync instead of the transition's.
    /// </summary>
    [Fact]
    public async Task Only_a_committed_resume_notifies()
    {
        var runId = await WorkflowRunSeeds.ParkedAtGateAsync(_store, pg);
        _observer.Clear();

        var refused = await _store.ResumeAsync(runId,
            new WorkflowResumeRequest { Signal = "not-the-signal", ResumedBy = TestPrincipals.Approver }, CancellationToken.None);
        refused.IsFailure.Should().BeTrue();
        _observer.Changes.Should().BeEmpty();

        var resumed = await _store.ResumeAsync(runId,
            new WorkflowResumeRequest { Signal = WorkflowRunSeeds.GateSignal, ResumedBy = TestPrincipals.Approver }, CancellationToken.None);
        resumed.IsSuccess.Should().BeTrue();
        _observer.Changes.Select(c => c.Change).Should().Equal(
            new WorkflowRunChange(runId, WorkflowRunChangeKind.Resumed, WorkflowStatus.Running, "done", null));
    }

    /// <summary>
    ///     Cancel notifies <c>Cancelled</c> once; cancelling the cancelled run again is a silent no-op that notifies
    ///     nothing.
    ///     Red: drop the NotifyAsync call in CancelAsync. Red: notify on the terminal early return.
    /// </summary>
    [Fact]
    public async Task Cancel_notifies_once()
    {
        var runId = await StartAsync("c1");
        _observer.Clear();

        await _store.CancelAsync(runId, "test", CancellationToken.None);
        await _store.CancelAsync(runId, "again", CancellationToken.None);

        _observer.Changes.Select(c => c.Change).Should().Equal(
            new WorkflowRunChange(runId, WorkflowRunChangeKind.Cancelled, WorkflowStatus.Cancelled, "implement", null));
    }

    /// <summary>
    ///     Fail notifies <c>Failed</c>; a stranded fail at a stale seq returns false and notifies nothing; a stranded
    ///     fail at the run's real seq returns true and notifies <c>Failed</c>.
    ///     Red: drop the NotifyAsync call in FailAsync. Red: notify when FailStrandedAsync returns false.
    ///     Red: drop the notify on FailStrandedAsync's success path.
    /// </summary>
    [Fact]
    public async Task Fail_notifies_and_a_refused_stranded_fail_does_not()
    {
        var failed = await StartAsync("c1");
        var stranded = await StartAsync("c2");
        _observer.Clear();

        await _store.FailAsync(failed, "boom", CancellationToken.None);
        (await _store.FailStrandedAsync(stranded, expectedSeq: 99, "stale", CancellationToken.None)).Should().BeFalse();
        // A new run is at seq 1.
        (await _store.FailStrandedAsync(stranded, expectedSeq: 1, "stranded", CancellationToken.None)).Should().BeTrue();

        _observer.Changes.Select(c => c.Change).Should().Equal(
            new WorkflowRunChange(failed, WorkflowRunChangeKind.Failed, WorkflowStatus.Failed, "implement", null),
            new WorkflowRunChange(stranded, WorkflowRunChangeKind.Failed, WorkflowStatus.Failed, "implement", null));
    }

    /// <summary>
    ///     A retry of a failed host-action node notifies <c>Retried</c>, Running, at that node.
    ///     Red: drop the NotifyAsync call in RetryFailedNodeAsync.
    /// </summary>
    [Fact]
    public async Task Retry_notifies()
    {
        var (runId, node, seq) = await WorkflowRunSeeds.FailedAtHostActionAsync(_store, pg);
        _observer.Clear();

        var retried = await _store.RetryFailedNodeAsync(runId,
            new WorkflowRetryRequest { ExpectedSeq = seq, RetriedBy = TestPrincipals.Approver }, CancellationToken.None);

        retried.IsSuccess.Should().BeTrue();
        _observer.Changes.Select(c => c.Change).Should().Equal(
            new WorkflowRunChange(runId, WorkflowRunChangeKind.Retried, WorkflowStatus.Running, node, null));
    }

    /// <summary>
    ///     An observer that throws, cancellation included, does not fail the change, does not stop the next observer,
    ///     and is logged with its type, the run, the change kind and the exception's type, never its message.
    ///     Red: let the exception escape NotifyAsync: StartAsync throws.
    ///     Red: return from NotifyAsync on the first failure: the second observer sees nothing.
    ///     Red: log the exception object or its message: the secret text appears in the log.
    ///     Red: drop the runId argument from LogObserverFailed. Red: drop the changeKind argument.
    ///     Red: add <c>when (exception is not OperationCanceledException)</c> to the catch: the cancellation case throws.
    /// </summary>
    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(OperationCanceledException))]
    public async Task A_throwing_observer_is_logged_by_type_and_ignored(Type exceptionType)
    {
        var after = new RecordingObserver(pg.ConnectionString);
        var store = NewStore(new ThrowingObserver((Exception)Activator.CreateInstance(exceptionType, "zqx-secret")!), after);

        var runId = await StartAsync("c1", store);

        after.Changes.Should().ContainSingle().Which.Change.RunId.Should().Be(runId);
        var line = _log.Lines.Should().ContainSingle().Subject;
        line.Should().Contain(exceptionType.Name).And.Contain(nameof(ThrowingObserver));
        line.Should().Contain(runId.ToString()).And.Contain(nameof(WorkflowRunChangeKind.Started));
        line.Should().NotContain("zqx-secret");
    }

    /// <summary>
    ///     Observers run one after another, in registration order: a first observer that yields still finishes before
    ///     the second starts.
    ///     Red: iterate _observers in reverse. Red: run the observers concurrently with Task.WhenAll.
    /// </summary>
    [Fact]
    public async Task Observers_run_in_registration_order_one_at_a_time()
    {
        var seen = new List<string>();
        var store = NewStore(new AppendingObserver(seen, "first", TimeSpan.FromMilliseconds(100)), new AppendingObserver(seen, "second", TimeSpan.Zero));

        await StartAsync("c1", store);

        seen.Should().Equal("first", "second");
    }

    /// <summary>
    ///     Each observer gets the caller's own token.
    ///     Red: pass CancellationToken.None in NotifyAsync.
    /// </summary>
    [Fact]
    public async Task Observers_get_the_callers_token()
    {
        using var cts = new CancellationTokenSource();

        await _store.StartAsync(new WorkflowStartRequest
        {
            Process = "manufacture",
            Version = 1,
            CorrelationKey = "c1",
            StartNode = "implement",
            InitialVariables = null,
            StartedBy = TestPrincipals.Starter,
        }, cts.Token);

        _observer.Tokens.Should().Equal(cts.Token);
    }

    /// <summary>
    ///     Observers run after the store has released its connection: with a pool of one, an observer that opens its
    ///     own connection from that same pool gets it, for a start, a completion and a cancel.
    ///     Red: notify inside StartCoreAsync before the connection's scope ends: the observer's open times out.
    ///     Red: the same inside CancelCoreAsync.
    /// </summary>
    [Fact]
    public async Task Observers_run_after_the_store_released_its_connection()
    {
        var poolOfOne = new NpgsqlConnectionStringBuilder(pg.ConnectionString) { MaxPoolSize = 1, Timeout = 2 }.ConnectionString;
        var observer = new RecordingObserver(poolOfOne);
        var store = new OrmWorkflowStore(new WorkflowOrmOptions { ConnectionString = poolOfOne },
            WorkflowRunSeeds.NewDefinitionStore(pg.ConnectionString), [observer], _log.For<OrmWorkflowStore>());

        var runId = await StartAsync("c1", store);
        await store.CompleteNodeAsync(runId, seq: 1,
            new WorkflowTransition("review", WorkflowStatus.Running, null, WorkflowEventKind.Completed),
            new NodeResult("ok", new Dictionary<string, object?>(StringComparer.Ordinal)), CancellationToken.None);
        await store.CancelAsync(runId, "test", CancellationToken.None);

        observer.Changes.Select(c => c.Change.Kind).Should().Equal(
            WorkflowRunChangeKind.Started, WorkflowRunChangeKind.NodeCompleted, WorkflowRunChangeKind.Cancelled);
        _log.Lines.Should().BeEmpty();
    }

    /// <summary>
    ///     <c>AddWorkflowOrm</c> hands every registered observer and the registered logger to
    ///     the store, so a change made through the resolved <see cref="IWorkflowStore"/> reaches them.
    ///     Red: drop the observers argument in AddWorkflowOrm: the recording observer sees nothing.
    ///     Red: drop the logger argument in AddWorkflowOrm: the throwing observer's failure is not logged.
    /// </summary>
    [Fact]
    public async Task AddWorkflowOrm_passes_the_registered_observers_and_logger()
    {
        var after = new RecordingObserver(pg.ConnectionString);
        var services = new ServiceCollection();
        services.AddSingleton<IWorkflowRunObserver>(new ThrowingObserver(new InvalidOperationException("zqx-secret")));
        services.AddSingleton<IWorkflowRunObserver>(after);
        services.AddSingleton(_log.For<OrmWorkflowStore>());
        services.AddThalos(thalos => thalos.AddWorkflowOrm(o =>
        {
            o.ConnectionString = pg.ConnectionString;
            o.EnsureSchemaOnStartup = false;
        }));
        await using var provider = services.BuildServiceProvider();

        var runId = await WorkflowRunSeeds.StartAsync(provider.GetRequiredService<OrmWorkflowStore>(), process: "manufacture", "c1");

        after.Changes.Should().ContainSingle().Which.Change.RunId.Should().Be(runId);
        _log.Lines.Should().ContainSingle().Which.Should().Contain(nameof(ThrowingObserver));
    }

    private OrmWorkflowStore NewStore(params IWorkflowRunObserver[] observers) =>
        new(new WorkflowOrmOptions { ConnectionString = pg.ConnectionString }, WorkflowRunSeeds.NewDefinitionStore(pg.ConnectionString),
            observers, _log.For<OrmWorkflowStore>());

    private Task<Guid> StartAsync(string correlation, OrmWorkflowStore? store = null) =>
        WorkflowRunSeeds.StartAsync(store ?? _store, process: "manufacture", correlation);

    private sealed class ThrowingObserver(Exception exception) : IWorkflowRunObserver
    {
        public ValueTask OnChangedAsync(WorkflowRunChange change, CancellationToken ct) => throw exception;
    }

    /// <summary>Waits <paramref name="delay"/>, yielding when it is not zero, then appends <paramref name="name"/>.</summary>
    private sealed class AppendingObserver(List<string> seen, string name, TimeSpan delay) : IWorkflowRunObserver
    {
        public async ValueTask OnChangedAsync(WorkflowRunChange change, CancellationToken ct)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }

            lock (seen)
            {
                seen.Add(name);
            }
        }
    }

    private sealed class RecordingObserver(string connectionString) : IWorkflowRunObserver
    {
        private readonly List<(WorkflowRunChange Change, bool VisibleOnAnotherConnection)> _changes = [];
        private readonly List<CancellationToken> _tokens = [];

        public IReadOnlyList<(WorkflowRunChange Change, bool VisibleOnAnotherConnection)> Changes => _changes;

        public IReadOnlyList<CancellationToken> Tokens => _tokens;

        public void Clear() => _changes.Clear();

        public async ValueTask OnChangedAsync(WorkflowRunChange change, CancellationToken ct)
        {
            _tokens.Add(ct);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM workflow_run WHERE id = @id", connection);
            cmd.Parameters.AddWithValue("id", change.RunId);
            var visible = (long)(await cmd.ExecuteScalarAsync(ct))! == 1;
            _changes.Add((change, visible));
        }
    }
}
