using Thalos.Workflow;
using ZeroAlloc.Results;

namespace Thalos.Tests.Workflow;

/// <summary>
/// In-memory <see cref="IWorkflowStore"/> for <see cref="ConstrainedOutcomeTests"/>. <c>Thalos.Workflow.Orm.OrmWorkflowStore</c>'s
/// transaction, concurrency and durability behaviour is already proven by Task 4's own suite against a real
/// PostgreSQL instance; this fake exists only so <see cref="WorkflowNodeDispatcher"/> can be tested in a unit
/// project with no Postgres and no Docker. It reproduces the parts of <see cref="IWorkflowStore"/>'s documented
/// contract the dispatcher tests actually exercise — the seq-checked completion, the visits-on-entry rule, the
/// dispatch enqueued for every transition that leaves a run Running (including the one <c>StartAsync</c> owes its
/// start node), and idempotent terminal calls — not the full ORM implementation's atomicity guarantees.
/// </summary>
/// <remarks>
/// The in-memory outbox below is a list of queued messages and nothing more: no retry, no backoff, no
/// dead-lettering, no redelivery of its own. It exists so a test can drive a run the way production does — take
/// the message the store actually enqueued and hand it to the dispatcher — instead of constructing that message
/// itself. A hand-built first message is precisely what let a store that never enqueued one look like it worked.
/// </remarks>
internal sealed class FakeWorkflowStore(IProcessDefinitionStore definitions) : IWorkflowStore
{
    private readonly Dictionary<Guid, WorkflowRun> _runs = [];
    private readonly List<WorkflowDispatchMessage> _outbox = [];
    private readonly List<WorkflowStartRequest> _startedRuns = [];

    /// <summary>How many dispatch messages are waiting to be taken, across every run.</summary>
    public int OutboxCount => _outbox.Count;

    /// <summary>
    /// Every <see cref="WorkflowStartRequest"/> this store has been asked to start, in call order — including
    /// idempotent calls whose correlation key was already taken and that therefore started nothing. Exists so a
    /// test can assert on what a caller passed to <see cref="StartAsync(WorkflowStartRequest,CancellationToken)"/>
    /// without hand-constructing the request itself, most importantly the <see cref="WorkflowStartRequest.Manifest"/>
    /// a caller pinned a run with.
    /// </summary>
    public IReadOnlyList<WorkflowStartRequest> StartedRuns => _startedRuns;

    /// <summary>
    /// Every <see cref="NodeResult"/> this store has been asked to complete a node with, in call order — exactly
    /// as <see cref="WorkflowNodeDispatcher"/> built it, including <see cref="NodeResult.Usage"/>. Exists so a
    /// test can assert on what the dispatcher handed the store without hand-constructing a result itself.
    /// </summary>
    public IReadOnlyList<NodeResult> CompletedResults => _completedResults;

    private readonly List<NodeResult> _completedResults = [];

    /// <summary>
    /// Takes the oldest queued dispatch message for <paramref name="runId"/>, or <see langword="null"/> when that
    /// run has none waiting. One store holds every test's runs, so taking is filtered by run rather than strictly
    /// FIFO across all of them — a real outbox consumer sees one shared table too, and the run id is exactly what
    /// tells one run's work from another's.
    /// </summary>
    public WorkflowDispatchMessage? TakeNext(Guid runId)
    {
        for (var i = 0; i < _outbox.Count; i++)
        {
            if (_outbox[i].RunId == runId)
            {
                var message = _outbox[i];
                _outbox.RemoveAt(i);
                return message;
            }
        }

        return null;
    }

    public ValueTask<Result<Guid>> StartAsync(WorkflowStartRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Every start names its starter (ruling R26): mirrors OrmWorkflowStore.StartAsync's own guard, checked
        // before this call records anything. No explicit paramName: CallerArgumentExpression supplies
        // "request.StartedBy" itself. A missing starter is a programming error and stays a throw, mirroring
        // OrmWorkflowStore.ValidateStartRequest — not the caller-triggerable failures below, which return a
        // Result instead.
        ArgumentNullException.ThrowIfNull(request.StartedBy);
        _startedRuns.Add(request);

        var process = request.Process;
        var version = request.Version;
        var startNode = request.StartNode;
        var initialVariables = request.InitialVariables;

        // Mirrors OrmWorkflowStore.StartAsync, which calls the same one check, now on the Result channel
        // instead of throwing.
        var overCapError = WorkflowVariableBlock.OverKeyLimitError(initialVariables);
        if (overCapError is not null)
        {
            return ValueTask.FromResult(Result<Guid>.Failure(overCapError));
        }

        // The host's own id when it supplied one, mirroring OrmWorkflowStore.StartAsync — otherwise this mints
        // one, exactly as it always has.
        var id = request.RunId ?? Guid.NewGuid();
        if (_runs.ContainsKey(id))
        {
            // Mirrors OrmWorkflowStore.StartAsync's workflow_run_pkey mapping: a caller-supplied RunId
            // colliding with a different, existing run is reported by name, and the existing run under that id
            // is left untouched — nothing above this point mutated _runs.
            return ValueTask.FromResult(Result<Guid>.Failure($"a run with id '{id}' already exists"));
        }

        _runs[id] = new WorkflowRun
        {
            Id = id,
            Process = process,
            ProcessVersion = version,
            CurrentNode = startNode,
            CurrentSeq = 1,
            Status = WorkflowStatus.Running,
            AwaitingSignal = null,
            Visits = new Dictionary<string, int>(StringComparer.Ordinal) { [startNode] = 1 },
            // Mirrors OrmWorkflowStore.StartAsync: a null seed and an empty one are the same thing, and both
            // leave the run with an empty bag rather than a null one.
            Variables = initialVariables is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : new Dictionary<string, object?>(initialVariables, StringComparer.Ordinal),
            // Written once, here, mirroring OrmWorkflowStore's InsertRunAsync — nothing below ever assigns
            // Manifest or StartedBy again, so FindAsync always returns exactly what this call was given.
            Manifest = request.Manifest,
            StartedBy = request.StartedBy,
        };

        // The start node's own dispatch, exactly as OrmWorkflowStore.StartAsync enqueues it. A fake that skipped
        // this would put these tests back to supplying a message production never produced.
        _outbox.Add(new WorkflowDispatchMessage(id, 1, startNode));
        return ValueTask.FromResult(Result<Guid>.Success(id));
    }

    /// <summary>
    /// <see cref="FindAsync"/> throws for this run id, the way a store whose database is unreachable does, so a
    /// caller's handling of a failed read can be tested.
    /// </summary>
    public Guid? ThrowOnFindFor { get; set; }

    public ValueTask<WorkflowRun?> FindAsync(Guid runId, CancellationToken ct) =>
        runId == ThrowOnFindFor
            ? throw new InvalidOperationException($"the fake store could not read run '{runId}'")
            : ValueTask.FromResult(_runs.GetValueOrDefault(runId));

    public ValueTask CompleteNodeAsync(Guid runId, long seq, WorkflowTransition transition, NodeResult result, CancellationToken ct)
    {
        var run = _runs[runId];

        // Same guard order as OrmWorkflowStore.CompleteNodeAsync: a run that left Running out-of-band, or a
        // stale/redelivered seq, both throw WorkflowConcurrencyException rather than silently applying a
        // transition against state that has moved on.
        if (run.Status is not WorkflowStatus.Running)
        {
            throw new WorkflowConcurrencyException($"Workflow run '{runId}' is {run.Status}, not Running.");
        }

        if (run.CurrentSeq != seq)
        {
            throw new WorkflowConcurrencyException($"Workflow run '{runId}' expected seq {run.CurrentSeq} but completion reported seq {seq}.");
        }

        _completedResults.Add(result);
        _runs[runId] = Apply(run, seq, transition, result.Variables);
        EnqueueIfRunning(_runs[runId], transition);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<Result> ResumeAsync(Guid runId, WorkflowResumeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Signal);
        // Every resume names its approver (ruling R20): mirrors OrmWorkflowStore.ResumeAsync's own guard,
        // checked before this call records anything. No explicit paramName: CallerArgumentExpression supplies
        // "request.ResumedBy" itself. A missing approver is a programming error and stays a throw, mirroring
        // OrmWorkflowStore — not the caller-triggerable failures below, which return a Result instead.
        ArgumentNullException.ThrowIfNull(request.ResumedBy);

        var signal = request.Signal;
        var payload = request.Payload;

        if (!_runs.TryGetValue(runId, out var run))
        {
            return Result.Failure($"Workflow run '{runId}' was not found.");
        }

        if (run.Status != WorkflowStatus.Awaiting || !string.Equals(run.AwaitingSignal, signal, StringComparison.Ordinal))
        {
            return Result.Failure($"Workflow run '{runId}' is not awaiting signal '{signal}'.");
        }

        // Resolved through the definition store on the run's pinned pair, mirroring OrmWorkflowStore.ResumeAsync
        // — the point of this fake is to stand in for that store's shape, and a fake that kept its own process
        // registry would be reproducing exactly the second source of truth the real one no longer has.
        var definition = await definitions.GetAsync(run.Process, run.ProcessVersion, ct);
        if (definition.IsFailure)
        {
            return Result.Failure(definition.Error);
        }

        var process = definition.Value;
        var variables = payload is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal) { ["payload"] = payload };

        var transition = WorkflowInterpreter.Advance(process, run, new NodeResult(null, variables));
        if (transition.IsFailure)
        {
            return Result.Failure(transition.Error);
        }

        var updated = Apply(run, run.CurrentSeq, transition.Value, variables);
        // Mirrors OrmWorkflowStore.RecordResumeAsync: recorded only once the transition itself has succeeded, so
        // a refused resume — signal mismatch or a failing Advance — leaves LastResume untouched.
        _runs[runId] = updated with { LastResume = new RunResume(request.ResumedBy, DateTimeOffset.UtcNow, signal) };
        EnqueueIfRunning(_runs[runId], transition.Value);
        return Result.Success();
    }

    /// <summary>
    /// Mirrors <c>OrmWorkflowStore.ApplyTransitionAsync</c>'s enqueue condition: a transition that leaves the run
    /// <see cref="WorkflowStatus.Running"/> schedules the node it landed on, and one that parks or terminates
    /// schedules nothing. <paramref name="updated"/> is read for the post-transition seq rather than recomputing
    /// it, so the queued message always names the seq a dispatcher's own guard will compare against.
    /// </summary>
    private void EnqueueIfRunning(WorkflowRun updated, WorkflowTransition transition)
    {
        if (transition.NextStatus == WorkflowStatus.Running)
        {
            _outbox.Add(new WorkflowDispatchMessage(updated.Id, updated.CurrentSeq, transition.NextNode));
        }
    }

    public ValueTask FailAsync(Guid runId, string errorMessage, CancellationToken ct)
    {
        var run = _runs[runId];
        if (IsTerminal(run.Status))
        {
            return ValueTask.CompletedTask;
        }

        _runs[runId] = run with { Status = WorkflowStatus.Failed, AwaitingSignal = null, LastError = errorMessage };
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> FailStrandedAsync(Guid runId, long expectedSeq, string errorMessage, CancellationToken ct)
    {
        var run = _runs[runId];
        if (run.CurrentSeq != expectedSeq || IsTerminal(run.Status))
        {
            return ValueTask.FromResult(false);
        }

        _runs[runId] = run with { Status = WorkflowStatus.Failed, AwaitingSignal = null, LastError = errorMessage };
        return ValueTask.FromResult(true);
    }

    public ValueTask CancelAsync(Guid runId, string reason, CancellationToken ct)
    {
        var run = _runs[runId];
        if (IsTerminal(run.Status))
        {
            return ValueTask.CompletedTask;
        }

        _runs[runId] = run with { Status = WorkflowStatus.Cancelled, AwaitingSignal = null, LastError = reason };
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<WorkflowRun>> FindStrandedAsync(TimeSpan olderThan, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<WorkflowRun>>([]);

    /// <summary>
    /// Puts a run in the store as given, for tests that need a status or a <see cref="WorkflowRun.LastResume"/> that
    /// no transition here produces — <see cref="RunWorkspaceSweeper"/>'s, which only read a run (ruling R12).
    /// </summary>
    /// <remarks>
    /// Not for dispatcher tests. Those start every run through <see cref="StartAsync(WorkflowStartRequest,CancellationToken)"/>
    /// and reach a node by being dispatched, which is what makes the visit counts the cap test reads the store's own
    /// work rather than a value the test wrote down.
    /// </remarks>
    public void Seed(WorkflowRun run) => _runs[run.Id] = run;

    private static WorkflowRun Apply(WorkflowRun run, long seq, WorkflowTransition transition, IReadOnlyDictionary<string, object?>? variables)
    {
        var visits = new Dictionary<string, int>(run.Visits, StringComparer.Ordinal);
        if (!string.Equals(transition.NextNode, run.CurrentNode, StringComparison.Ordinal))
        {
            visits[transition.NextNode] = visits.GetValueOrDefault(transition.NextNode) + 1;
        }

        var mergedVariables = new Dictionary<string, object?>(run.Variables, StringComparer.Ordinal);
        if (variables is not null)
        {
            foreach (var (key, value) in variables)
            {
                mergedVariables[key] = value;
            }
        }

        return run with
        {
            CurrentNode = transition.NextNode,
            CurrentSeq = seq + 1,
            Status = transition.NextStatus,
            AwaitingSignal = transition.AwaitingSignal,
            Visits = visits,
            Variables = mergedVariables,
        };
    }

    private static bool IsTerminal(WorkflowStatus status) =>
        status is WorkflowStatus.Succeeded or WorkflowStatus.Failed or WorkflowStatus.Cancelled;
}
