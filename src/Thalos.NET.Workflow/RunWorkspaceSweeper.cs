using Microsoft.Extensions.Logging;
using Thalos.Workspaces;

namespace Thalos.Workflow;

/// <summary>
/// Removes a run's workspace once nothing needs it. Kept: <see cref="WorkflowStatus.Running"/> and
/// <see cref="WorkflowStatus.Awaiting"/> runs, and <see cref="WorkflowStatus.Failed"/> or
/// <see cref="WorkflowStatus.Cancelled"/> runs with a <see cref="WorkflowRun.LastResume"/> — a run that failed after
/// approval keeps its worktree for a human. Removed: <see cref="WorkflowStatus.Succeeded"/> runs, whether or not they
/// were resumed, because <c>open-pull-request</c> has already pushed and opened the PR; Failed or Cancelled runs with
/// no <see cref="WorkflowRun.LastResume"/>; and workspaces whose run row does not exist, once their
/// <see cref="RunWorkspace.CreatedAt"/> is older than <see cref="OrphanGrace"/>. Before that decision, a provider that
/// is an <see cref="IParkableRunWorkspaceProvider"/> parks the workspace of every run that exists and is not
/// <see cref="WorkflowStatus.Running"/>.
/// </summary>
/// <remarks>
/// <para>
/// A plain class with no timer of its own, like <see cref="WorkflowRunReconciler"/>: hosting
/// <see cref="SweepAsync"/> on a schedule is the consumer's job. <c>open-pull-request</c> never removes the workspace
/// itself — a crash between its PR and the run's advance would otherwise leave a redelivery with no workspace — so a
/// Succeeded run's workspace goes at most one sweep interval later.
/// </para>
/// <para>
/// <b>Decided on run state, never on the workspace record's state.</b> <see cref="IRunWorkspaceProvider.ListAsync"/>
/// carries no record state, and the sweeper does not need any: <see cref="IRunWorkspaceProvider.RemoveAsync"/> is
/// the authority that refuses an unsafe removal — a provisional record whose create is still live — and finishes one
/// a crashed claimant or an earlier failed removal left behind. A refusal is a failed removal like any other: skipped,
/// logged, and retried by the next sweep.
/// </para>
/// <para>
/// <b>Check, then act.</b> Each workspace's run is read from <see cref="IWorkflowStore"/> immediately before that
/// workspace's removal, never from a snapshot taken for the whole sweep. The only states that lead to removal are
/// the terminal ones. <see cref="IWorkflowStore"/> never moves a run out of Succeeded or Cancelled, so a run read as
/// either stays removable. A run read as Failed with no <see cref="WorkflowRun.LastResume"/> is the exception: the
/// store's retry can move it back to Running between the read and the removal, and that retry then finds no
/// workspace, so its action fails the run again. That fails closed, and loses no work that was not already
/// unrecoverable without the workspace. A Failed or Cancelled run can still never gain a
/// <see cref="WorkflowRun.LastResume"/>, because only a run <see cref="WorkflowStatus.Awaiting"/> can be resumed. The
/// one other decision with a window left is the orphan's: a run
/// row written for a workspace older than <see cref="OrphanGrace"/> after the sweep read none. That is a host that
/// stalled for longer than the grace period between creating the worktree and starting the run, and it fails
/// closed: the run finds no workspace and its write-granted nodes are refused.
/// </para>
/// </remarks>
public sealed partial class RunWorkspaceSweeper(
    IRunWorkspaceProvider workspaces,
    IWorkflowStore store,
    TimeProvider clock,
    ILogger<RunWorkspaceSweeper> logger)
{
    /// <summary>
    /// A workspace exists before its run row does: the host creates the worktree, then starts the run under its id.
    /// A sweep inside that window must not remove it (ruling R9). An alias of <see cref="RunWorkspaceGrace.Orphan"/>,
    /// the one definition the sandbox provider's reconcile shares.
    /// </summary>
    public static readonly TimeSpan OrphanGrace = RunWorkspaceGrace.Orphan;

    /// <summary>
    /// The most time one sweep spends parking. Parks run one after another and each may take a sandbox export and a
    /// lock wait, so once this is spent the park in progress is cancelled and the rest wait for the next sweep. Each park
    /// is told what is left, so a provider does not begin one it cannot finish in time, and each sweep starts one
    /// workspace further on in the list, so the same runs are not left over every time. Five minutes.
    /// </summary>
    public TimeSpan ParkBudget { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How many sweeps have started, less one: where the next sweep starts in the list.</summary>
    private int _sweeps = -1;

    private readonly IRunWorkspaceProvider _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
    private readonly IWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Removes every listed workspace whose run no longer needs it, and returns how many were removed. A removal that
    /// fails — refused by <see cref="IRunWorkspaceProvider.RemoveAsync"/>, or throwing while the run is read or the
    /// workspace removed — is logged and skipped, never thrown, and the sweep goes on to the next workspace; the next
    /// sweep retries it.
    /// </summary>
    /// <param name="ct">
    /// Cancels the sweep. An <see cref="OperationCanceledException"/> propagates only when this token is cancelled;
    /// one raised for any other reason, such as an observer's own timeout during a removal, is a failed removal and
    /// is skipped like any other.
    /// </param>
    /// <returns>The number of workspaces this sweep removed.</returns>
    public async ValueTask<int> SweepAsync(CancellationToken ct)
    {
        var listed = await _workspaces.ListAsync(ct).ConfigureAwait(false);

        // Each sweep starts one workspace further on, so a park budget spent early in the list never starves the same tail.
        var start = listed.Count == 0 ? 0 : (int)((uint)Interlocked.Increment(ref _sweeps) % (uint)listed.Count);
        var removed = 0;
        var parkingStarted = _clock.GetTimestamp();
        var unparked = 0;
        for (var i = 0; i < listed.Count; i++)
        {
            var workspace = listed[(start + i) % listed.Count];
            var parkBudget = ParkBudget - _clock.GetElapsedTime(parkingStarted);
            if (await ParkIfNotRunningAsync(workspace.RunId, parkBudget, ct).ConfigureAwait(false) == ParkOutcome.Skipped)
            {
                unparked++;
            }

            try
            {
                if (!await NoLongerNeededAsync(workspace, ct).ConfigureAwait(false))
                {
                    continue;
                }

                var removal = await _workspaces.RemoveAsync(workspace.RunId, ct).ConfigureAwait(false);
                if (removal.IsFailure)
                {
                    LogRemovalRefused(_logger, workspace.RunId, removal.Error.ToString());
                    continue;
                }

                removed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                LogRemovalFailed(_logger, workspace.RunId, ex);
            }
        }

        if (unparked > 0)
        {
            LogParkBudgetSpent(_logger, ParkBudget, unparked);
        }

        return removed;
    }

    /// <summary>
    /// Parks the workspace of a run that exists and is not <see cref="WorkflowStatus.Running"/>, when the provider can
    /// park: a run at its gate, or ended, needs nothing its workspace runs. A failed park is logged and the sweep goes
    /// on; the next sweep retries it. Whether the workspace is then removed is decided on run state alone, as before.
    /// The park is cancelled once <paramref name="budget"/> runs out, and not started when none is left.
    /// </summary>
    private async ValueTask<ParkOutcome> ParkIfNotRunningAsync(Guid runId, TimeSpan budget, CancellationToken ct)
    {
        if (_workspaces is not IParkableRunWorkspaceProvider parkable)
        {
            return ParkOutcome.NotNeeded;
        }

        try
        {
            var run = await _store.FindAsync(runId, ct).ConfigureAwait(false);
            if (run is null || run.Status == WorkflowStatus.Running)
            {
                return ParkOutcome.NotNeeded;
            }

            if (budget <= TimeSpan.Zero)
            {
                return ParkOutcome.Skipped;
            }

            using var remaining = new CancellationTokenSource(budget, _clock);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, remaining.Token);
            var parked = await parkable.ParkAsync(runId, budget, bounded.Token).ConfigureAwait(false);
            if (parked.IsFailure)
            {
                LogParkFailed(_logger, runId, parked.Error.ToString(), exception: null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogParkFailed(_logger, runId, ex.Message, ex);
        }

        return ParkOutcome.Attempted;
    }

    private async ValueTask<bool> NoLongerNeededAsync(RunWorkspace workspace, CancellationToken ct)
    {
        // Read here, per workspace and right before its removal, not once for the whole sweep: see the class remarks.
        var run = await _store.FindAsync(workspace.RunId, ct).ConfigureAwait(false);
        if (run is null)
        {
            return _clock.GetUtcNow() - workspace.CreatedAt > OrphanGrace;
        }

        return run.Status switch
        {
            WorkflowStatus.Succeeded => true,
            WorkflowStatus.Failed or WorkflowStatus.Cancelled => run.LastResume is null,
            // Running, Awaiting, and any status added later: kept. A workspace kept by mistake costs disk until a
            // human looks; one removed by mistake costs the run.
            _ => false,
        };
    }

    [LoggerMessage(
        EventId = 910,
        Level = LogLevel.Warning,
        Message = "Run workspace sweep: the workspace of run {RunId} was not removed: {Error}. The next sweep retries it.")]
    private static partial void LogRemovalRefused(ILogger logger, Guid runId, string error);

    [LoggerMessage(
        EventId = 911,
        Level = LogLevel.Warning,
        Message = "Run workspace sweep: removing the workspace of run {RunId} failed. The next sweep retries it.")]
    private static partial void LogRemovalFailed(ILogger logger, Guid runId, Exception exception);

    [LoggerMessage(
        EventId = 912,
        Level = LogLevel.Warning,
        Message = "Run workspace sweep: parking the workspace of run {RunId} failed: {Error}. The next sweep retries it.")]
    private static partial void LogParkFailed(ILogger logger, Guid runId, string error, Exception? exception);

    [LoggerMessage(
        EventId = 913,
        Level = LogLevel.Warning,
        Message = "Run workspace sweep: the park budget of {Budget} was spent; {Count} workspaces were not parked and wait for the next sweep.")]
    private static partial void LogParkBudgetSpent(ILogger logger, TimeSpan budget, int count);

    /// <summary>What the park step did for one workspace.</summary>
    private enum ParkOutcome
    {
        /// <summary>The provider cannot park, or the run is running or has no row.</summary>
        NotNeeded,

        /// <summary>A park was made, whatever its outcome.</summary>
        Attempted,

        /// <summary>A park was needed but the sweep's park budget was spent.</summary>
        Skipped,
    }
}
