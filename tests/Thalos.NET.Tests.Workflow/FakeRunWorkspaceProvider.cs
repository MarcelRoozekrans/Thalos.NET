using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Tests.Workflow;

/// <summary>
/// In-memory <see cref="IRunWorkspaceProvider"/> for <see cref="RunWorkspaceSweeperTests"/> (ruling R12). It holds
/// what <see cref="Add"/> stored, lists it in insertion order, and records every successful removal; it creates
/// nothing. <c>GitWorktreeWorkspaceProvider</c>'s own suite proves the real removal's refusals and retries.
/// </summary>
internal class FakeRunWorkspaceProvider : IRunWorkspaceProvider
{
    private readonly List<RunWorkspace> _workspaces = [];

    /// <summary>The run ids whose workspaces <see cref="RemoveAsync"/> removed.</summary>
    public HashSet<Guid> Removed { get; } = [];

    /// <summary><see cref="RemoveAsync"/> returns a failure for this run id and removes nothing.</summary>
    public Guid? FailRemovalFor { get; set; }

    /// <summary>
    /// Runs at the start of every <see cref="RemoveAsync"/> call, with its run id and token, before anything is
    /// removed: a test throws from it to make a removal throw, or changes the store to act between two removals.
    /// </summary>
    public Func<Guid, CancellationToken, ValueTask>? BeforeRemoval { get; set; }

    /// <summary>Records <paramref name="workspace"/>; it is listed and found until removed.</summary>
    public void Add(RunWorkspace workspace) => _workspaces.Add(workspace);

    public ValueTask<Result<RunWorkspace, AgentError>> CreateAsync(RunWorkspaceRequest request, CancellationToken ct) =>
        ValueTask.FromResult(Result<RunWorkspace, AgentError>.Failure(AgentError.Validation("not supported by the fake")));

    public ValueTask<RunWorkspace?> FindAsync(Guid runId, CancellationToken ct) =>
        ValueTask.FromResult(_workspaces.Find(w => w.RunId == runId));

    public ValueTask<IReadOnlyList<RunWorkspace>> ListAsync(CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<RunWorkspace>>([.. _workspaces]);

    public async ValueTask<UnitResult<AgentError>> RemoveAsync(Guid runId, CancellationToken ct)
    {
        if (BeforeRemoval is { } before)
        {
            await before(runId, ct);
        }

        if (runId == FailRemovalFor)
        {
            return UnitResult<AgentError>.Failure(AgentError.Validation("the fake refused the removal"));
        }

        _workspaces.RemoveAll(w => w.RunId == runId);
        Removed.Add(runId);
        return UnitResult<AgentError>.Success();
    }
}

/// <summary>
/// A <see cref="FakeRunWorkspaceProvider"/> that can park, for the sweeper's park step: it records every park, and
/// refuses one for <see cref="FailParkFor"/>.
/// </summary>
internal sealed class FakeParkableRunWorkspaceProvider : FakeRunWorkspaceProvider, IParkableRunWorkspaceProvider
{
    /// <summary>The run ids <see cref="ParkAsync"/> parked, in order.</summary>
    public List<Guid> Parked { get; } = [];

    /// <summary>Every run id <see cref="ParkAsync"/> was called for, in order, whatever came of it.</summary>
    public List<Guid> ParkAttempts { get; } = [];

    /// <summary><see cref="ParkAsync"/> returns a failure for this run id and parks nothing.</summary>
    public Guid? FailParkFor { get; set; }

    /// <summary>Awaited at the start of every <see cref="ParkAsync"/>, with its run id and token: a test hangs a park with it.</summary>
    public Func<Guid, CancellationToken, Task>? BeforePark { get; set; }

    /// <summary>The budget each budgeted <see cref="ParkAsync(Guid, TimeSpan, CancellationToken)"/> was given, in order.</summary>
    public List<TimeSpan> Budgets { get; } = [];

    public ValueTask<UnitResult<AgentError>> ParkAsync(Guid runId, TimeSpan budget, CancellationToken ct)
    {
        Budgets.Add(budget);
        return ParkAsync(runId, ct);
    }

    public async ValueTask<UnitResult<AgentError>> ParkAsync(Guid runId, CancellationToken ct)
    {
        ParkAttempts.Add(runId);
        if (BeforePark is { } before)
        {
            await before(runId, ct);
        }

        if (runId == FailParkFor)
        {
            return UnitResult<AgentError>.Failure(AgentError.ProviderError("the fake could not export"));
        }

        Parked.Add(runId);
        return UnitResult<AgentError>.Success();
    }
}
