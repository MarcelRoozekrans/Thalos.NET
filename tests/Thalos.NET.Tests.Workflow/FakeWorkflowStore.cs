using Thalos.Workflow;
using ZeroAlloc.Results;

namespace Thalos.Tests.Workflow;

/// <summary>
/// In-memory <see cref="IWorkflowStore"/> for <see cref="ConstrainedOutcomeTests"/>. <c>Thalos.Workflow.Orm.OrmWorkflowStore</c>'s
/// transaction, concurrency and durability behaviour is already proven by Task 4's own suite against a real
/// PostgreSQL instance; this fake exists only so <see cref="WorkflowNodeDispatcher"/> can be tested in a unit
/// project with no Postgres and no Docker. It reproduces the parts of <see cref="IWorkflowStore"/>'s documented
/// contract the dispatcher tests actually exercise — the seq-checked completion, the visits-on-entry rule, and
/// idempotent terminal calls — not the full ORM implementation's atomicity guarantees.
/// </summary>
internal sealed class FakeWorkflowStore(IProcessDefinitionStore definitions) : IWorkflowStore
{
    private readonly Dictionary<Guid, WorkflowRun> _runs = [];

    public ValueTask<Guid> StartAsync(string process, int version, string correlationKey, string startNode, CancellationToken ct)
    {
        var id = Guid.NewGuid();
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
        };

        return ValueTask.FromResult(id);
    }

    public ValueTask<WorkflowRun?> FindAsync(Guid runId, CancellationToken ct) =>
        ValueTask.FromResult(_runs.GetValueOrDefault(runId));

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

        _runs[runId] = Apply(run, seq, transition, result.Variables);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<Result> ResumeAsync(Guid runId, string signal, string? payload, CancellationToken ct)
    {
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

        _runs[runId] = Apply(run, run.CurrentSeq, transition.Value, variables);
        return Result.Success();
    }

    public ValueTask FailAsync(Guid runId, string errorMessage, CancellationToken ct)
    {
        var run = _runs[runId];
        if (IsTerminal(run.Status))
        {
            return ValueTask.CompletedTask;
        }

        _runs[runId] = run with { Status = WorkflowStatus.Failed, LastError = errorMessage };
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> FailStrandedAsync(Guid runId, long expectedSeq, string errorMessage, CancellationToken ct)
    {
        var run = _runs[runId];
        if (run.CurrentSeq != expectedSeq || IsTerminal(run.Status))
        {
            return ValueTask.FromResult(false);
        }

        _runs[runId] = run with { Status = WorkflowStatus.Failed, LastError = errorMessage };
        return ValueTask.FromResult(true);
    }

    public ValueTask CancelAsync(Guid runId, string reason, CancellationToken ct)
    {
        var run = _runs[runId];
        if (IsTerminal(run.Status))
        {
            return ValueTask.CompletedTask;
        }

        _runs[runId] = run with { Status = WorkflowStatus.Cancelled, LastError = reason };
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<WorkflowRun>> FindStrandedAsync(TimeSpan olderThan, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<WorkflowRun>>([]);

    /// <summary>Seeds a run directly, bypassing <see cref="StartAsync"/>, for a test that wants full control over its starting shape.</summary>
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
