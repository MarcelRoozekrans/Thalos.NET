using Thalos.Git.Workspaces;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Sandbox;

public sealed partial class SandboxRunWorkspaceProvider
{
    // ---------- park, hand-off ----------

    /// <summary>
    /// How long a park or checkout waits for another call that holds the run's lock: another park, which may start the
    /// sandbox again, bounded by <see cref="SandboxOptions.RestartTimeout"/>, and export it, bounded by
    /// <see cref="SandboxOptions.ExportTimeout"/>, or a checkout; plus five minutes for their engine and git work. Tests
    /// shorten it.
    /// </summary>
    internal TimeSpan RunLockWait { get; set; } = options.ExportTimeout + options.RestartTimeout + TimeSpan.FromMinutes(5);

    /// <summary>How many parks may start an exited sandbox again to export it before the run is parked without a patch.</summary>
    internal const int MaxRestartAttempts = 2;

    /// <summary>The reason a run is parked without a patch when its sandbox was gone.</summary>
    internal const string LostReason = "it no longer existed";

    /// <summary>The reason a run is parked without a patch when its exited sandbox could not be exported.</summary>
    internal const string RestartReason = "could not be restarted to export";

    /// <summary>
    /// Parks the run with no budget: <see cref="ParkAsync(Guid, TimeSpan, CancellationToken)"/> with
    /// <see cref="Timeout.InfiniteTimeSpan"/>. Stops the run's sandbox, keeping its patch for publishing. Idempotent.
    /// </summary>
    /// <param name="runId">The run.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// <para>
    /// A parked run succeeds at once. A ready or exporting one is marked exporting; its patch is streamed under
    /// <see cref="SandboxOptions.ExportTimeout"/> to <c>&lt;DataRoot&gt;/sandboxes/&lt;id&gt;.patch.tmp</c>, refused past
    /// <see cref="PatchApplyLimits.MaxPatchBytes"/>, and moved to <c>.patch</c>; then the sandbox is deleted, the
    /// observers are told it is going, under the run's lock, and the record is marked parked with its patch.
    /// </para>
    /// <para>
    /// <b>A failure keeps the sandbox.</b> A failed export, move or delete leaves the record exporting and fails the
    /// call; the next park, from the next sweep or a checkout, starts again. A provisional, removing or absent record
    /// has nothing to park and fails.
    /// </para>
    /// <para>
    /// <b>A lost sandbox.</b> Only a container the runtime reports <see cref="SandboxState.Missing"/> is lost: the run is
    /// parked with <see cref="SandboxRecord.PatchMissing"/>, unless an earlier park of this record already stored its
    /// patch. A null answer is not a verdict, since the runtime answers null when its engine cannot be asked too; it
    /// fails the park, which the next one retries.
    /// </para>
    /// <para>
    /// <b>An exited sandbox</b> is started again with <see cref="ISandboxRuntime.StartAsync"/>, waited for, up to
    /// <see cref="SandboxOptions.RestartTimeout"/>, until its host answers, then exported against the record's base
    /// commit and deleted. Each such attempt is counted in <see cref="SandboxRecord.ExportAttempts"/> before it is made,
    /// and given back when the caller cancels it; once <see cref="MaxRestartAttempts"/> have been made, the next park
    /// parks the run without a patch, with the reason <see cref="RestartReason"/>, unless an earlier attempt already
    /// stored its patch, so no record stays exporting forever. The sandbox's volume is never read from the host.
    /// </para>
    /// </remarks>
    public ValueTask<UnitResult<AgentError>> ParkAsync(Guid runId, CancellationToken ct) => ParkAsync(runId, Timeout.InfiniteTimeSpan, ct);

    /// <inheritdoc />
    /// <remarks>
    /// As <see cref="ParkAsync(Guid, CancellationToken)"/>, which passes <see cref="Timeout.InfiniteTimeSpan"/>, except
    /// that a park that would start an exited sandbox again is not begun unless <paramref name="budget"/>, less the wait
    /// for the run's lock, still covers <see cref="SandboxOptions.RestartTimeout"/> plus
    /// <see cref="SandboxOptions.ExportTimeout"/>. It fails instead, before it writes anything, and a later park tries again.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="budget"/> is negative and not <see cref="Timeout.InfiniteTimeSpan"/>.</exception>
    public ValueTask<UnitResult<AgentError>> ParkAsync(Guid runId, TimeSpan budget, CancellationToken ct)
    {
        if (budget != Timeout.InfiniteTimeSpan)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(budget, TimeSpan.Zero);
        }

        var now = clock.GetUtcNow();
        DateTimeOffset? deadline = budget == Timeout.InfiniteTimeSpan || budget >= DateTimeOffset.MaxValue - now ? null : now + budget;
        return ParkCoreAsync(runId, deadline, ct);
    }

    private async ValueTask<UnitResult<AgentError>> ParkCoreAsync(Guid runId, DateTimeOffset? deadline, CancellationToken ct)
    {
        // Settled without the lock when there is nothing to do, so a sweep never waits on another call for these.
        var read = await _store.ReadAsync(runId, ct).ConfigureAwait(false);
        if (read.Error is { } error)
        {
            return UnitResult<AgentError>.Failure(Unreadable(runId, error));
        }

        switch (read.Record?.State)
        {
            case SandboxRecordState.Parked:
                return UnitResult<AgentError>.Success();
            case SandboxRecordState.Ready or SandboxRecordState.Exporting:
                break;
            default:
                return UnitResult<AgentError>.Failure(NothingToPark(runId, read.Record?.State));
        }

        var runLock = await LockRunAsync(runId, ct).ConfigureAwait(false);
        if (runLock.IsFailure)
        {
            return UnitResult<AgentError>.Failure(runLock.Error);
        }

        try
        {
            return await ParkLockedAsync(runId, deadline, ct).ConfigureAwait(false);
        }
        finally
        {
            ReleaseRunLock(runLock.Value, runId, deleteFile: !File.Exists(_store.RecordPath(runId)));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Parks the run, then applies its stored patch to a clean worktree of the trusted publish provider, cut from the
    /// record's base commit, under <c>&lt;DataRoot&gt;/publish/runs/&lt;id&gt;</c>. No tree a sandboxed process touched
    /// is ever checked out (S4). The patch is applied with <see cref="GitPatchApplier"/>, which refuses a patch that
    /// touches a protected path (S5); the refused worktree is removed, so nothing is left to publish.
    /// </para>
    /// <para>
    /// <b>Commit the staged index.</b> The applier applies the patch with <c>git apply --index</c>, so the returned
    /// worktree holds the run's change staged, and that index is what the S5 check passed. Host code must commit it with
    /// <see cref="Thalos.Git.GitCommitRequest.CommitStagedIndex"/>, which commits the index as it stands, less
    /// <see cref="Thalos.Git.GitCommitRequest.ExcludePaths"/>. A commit that stages from disk again would drop an added
    /// file the worktree's <c>.gitignore</c> matches and, under <c>core.fileMode=false</c>, a mode change. A file the host
    /// writes into the worktree afterwards, such as standing instructions, is not staged by that commit; a later
    /// path-scoped commit can take it. List such a file in <see cref="SandboxOptions.ProtectedPaths"/> too, so the
    /// applier refuses a patch that touches it: an excluded path the patch changed is otherwise dropped from the first
    /// commit but left on disk, and the host's path-scoped commit then publishes the patch's version under the host's
    /// message.
    /// </para>
    /// <para>
    /// <b>Idempotent.</b> A worktree this call finished before is returned as it is, so a file written in it since, such
    /// as the standing instructions a resume writes, survives. A worktree an earlier call made but never finished
    /// applying to is removed and rebuilt, never returned. A run whose sandbox was lost before its export fails.
    /// </para>
    /// </remarks>
    public async ValueTask<Result<RunWorkspace, AgentError>> CheckoutForPublishAsync(Guid runId, CancellationToken ct)
    {
        // A run with no record fails before its lock is taken, so no lock file is left for a run that does not exist.
        var known = await _store.ReadAsync(runId, ct).ConfigureAwait(false);
        if (known.Record is null && known.Error is null)
        {
            return Result<RunWorkspace, AgentError>.Failure(NothingToPark(runId, state: null));
        }

        var runLock = await LockRunAsync(runId, ct).ConfigureAwait(false);
        if (runLock.IsFailure)
        {
            return Result<RunWorkspace, AgentError>.Failure(runLock.Error);
        }

        try
        {
            return await CheckoutLockedAsync(runId, ct).ConfigureAwait(false);
        }
        finally
        {
            // Still under the lock: a remove that ran while this call waited took the record, and its lock file, with it;
            // the file this call opened again goes too.
            ReleaseRunLock(runLock.Value, runId, deleteFile: !File.Exists(_store.RecordPath(runId)));
        }
    }

    private async Task<UnitResult<AgentError>> ParkLockedAsync(Guid runId, DateTimeOffset? deadline, CancellationToken ct)
    {
        var read = await _store.ReadAsync(runId, ct).ConfigureAwait(false);
        if (read.Error is { } error)
        {
            return UnitResult<AgentError>.Failure(Unreadable(runId, error));
        }

        if (read.Record is { State: SandboxRecordState.Parked })
        {
            return UnitResult<AgentError>.Success();
        }

        if (read.Record is not { State: SandboxRecordState.Ready or SandboxRecordState.Exporting } record)
        {
            return UnitResult<AgentError>.Failure(NothingToPark(runId, read.Record?.State));
        }

        var handle = await FindSandboxAsync(record, ct).ConfigureAwait(false);
        if (handle.IsFailure)
        {
            return UnitResult<AgentError>.Failure(handle.Error);
        }

        // Decided before anything is written, so a park that is deferred leaves the record as it found it.
        var plan = Plan(record, handle.Value);
        if (plan == ParkPlan.Restart && Deferred(record, deadline) is { } deferred)
        {
            return UnitResult<AgentError>.Failure(deferred);
        }

        var exporting = record with { State = SandboxRecordState.Exporting };
        if (record.State != SandboxRecordState.Exporting)
        {
            var marked = await _store.WriteAsync(exporting, ct).ConfigureAwait(false);
            if (marked.IsFailure)
            {
                return marked;
            }
        }

        var stored = await StorePatchAsync(exporting, plan, handle.Value, ct).ConfigureAwait(false);
        if (stored.IsFailure)
        {
            return UnitResult<AgentError>.Failure(stored.Error);
        }

        return await FinishParkAsync(exporting, stored.Value.Record, stored.Value.MissingReason, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes the sandbox, tells the observers it is going, and records the run parked, with its patch or why it has none.</summary>
    private async Task<UnitResult<AgentError>> FinishParkAsync(SandboxRecord exporting, SandboxRecord current, string? missing, CancellationToken ct)
    {
        var runId = exporting.RunId;
        var deleted = await runtime.DeleteAsync(exporting.SandboxId, ct).ConfigureAwait(false);
        if (deleted.IsFailure)
        {
            return deleted;
        }

        await NotifyObserversAsync(Workspace(exporting), removing: true, ct).ConfigureAwait(false);
        return await _store.WriteAsync(
            current with
            {
                State = SandboxRecordState.Parked,
                PatchPath = missing is null ? _store.PatchPath(runId) : null,
                PatchMissing = missing is not null,
                PatchMissingReason = missing,
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The record's sandbox, as the runtime reports it. Null is not a verdict: the runtime answers null when its engine
    /// cannot be asked too, so only an explicit <see cref="SandboxState.Missing"/> ever counts as lost.
    /// </summary>
    private async Task<Result<SandboxHandle, AgentError>> FindSandboxAsync(SandboxRecord record, CancellationToken ct)
    {
        var handle = await runtime.GetAsync(record.SandboxId, ct).ConfigureAwait(false);
        if (handle is null)
        {
            return Result<SandboxHandle, AgentError>.Failure(AgentError.ProviderError(
                $"The sandbox of run '{record.RunId}' could not be found, or the runtime could not be asked; its record is kept for the next park."));
        }

        return handle.RunId == record.RunId
            ? Result<SandboxHandle, AgentError>.Success(handle)
            : Result<SandboxHandle, AgentError>.Failure(AgentError.ProviderError(OtherRun));
    }

    /// <summary>What a park of a ready or exporting record does, decided from the record and its sandbox.</summary>
    private enum ParkPlan
    {
        /// <summary>An earlier park of this exporting record already stored its patch: nothing to export.</summary>
        KeepStored,

        /// <summary>The sandbox is missing and no patch is stored.</summary>
        Lost,

        /// <summary>The sandbox needs another restart, and <see cref="MaxRestartAttempts"/> have been made.</summary>
        Exhausted,

        /// <summary>The sandbox exited, or a park started it again, and has restart attempts left.</summary>
        Restart,

        /// <summary>The sandbox runs as created: export it.</summary>
        Export,
    }

    /// <summary>
    /// The plan for <paramref name="record"/>. A stored patch under an exporting record is a finished export whose park
    /// stopped before the record was written; a ready record has none, since a remove deletes a run's patch before its
    /// record. It needs no export and no restart. A sandbox a park started again stays under the restart rules, however
    /// it runs now, so its failing exports are counted.
    /// </summary>
    private ParkPlan Plan(SandboxRecord record, SandboxHandle handle)
    {
        if (record.State == SandboxRecordState.Exporting && File.Exists(_store.PatchPath(record.RunId)))
        {
            return ParkPlan.KeepStored;
        }

        if (handle.State == SandboxState.Missing)
        {
            return ParkPlan.Lost;
        }

        if (handle.State == SandboxState.Exited || record.RestartedByPark || record.ExportAttempts > 0)
        {
            return record.ExportAttempts >= MaxRestartAttempts ? ParkPlan.Exhausted : ParkPlan.Restart;
        }

        return ParkPlan.Export;
    }

    /// <summary>Why a restart is not begun before <paramref name="deadline"/>, or null when it fits.</summary>
    private AgentError? Deferred(SandboxRecord record, DateTimeOffset? deadline)
    {
        var needed = options.RestartTimeout + options.ExportTimeout;
        return deadline is { } by && by - clock.GetUtcNow() < needed
            ? AgentError.ProviderError($"The park of run '{record.RunId}' must start its sandbox again, which may take {needed}, more than the caller has left; a later park tries again.")
            : null;
    }

    /// <summary>
    /// Carries out <paramref name="plan"/>. Succeeds with the record as it now stands and, when there is no patch, why:
    /// <see cref="LostReason"/> or <see cref="RestartReason"/>. Fails, for the next park to retry, when an export fails.
    /// </summary>
    private async Task<Result<(SandboxRecord Record, string? MissingReason), AgentError>> StorePatchAsync(
        SandboxRecord record, ParkPlan plan, SandboxHandle handle, CancellationToken ct)
    {
        switch (plan)
        {
            case ParkPlan.KeepStored:
                return Result<(SandboxRecord, string?), AgentError>.Success((record, null));
            case ParkPlan.Lost:
                LogLostBeforeExport(logger, record.RunId, record.SandboxId);
                return Result<(SandboxRecord, string?), AgentError>.Success((record, LostReason));
            case ParkPlan.Exhausted:
                LogRestartsExhausted(logger, record.RunId, record.SandboxId, record.ExportAttempts);
                return Result<(SandboxRecord, string?), AgentError>.Success((record, RestartReason));
            case ParkPlan.Restart:
                return await RestartAndExportAsync(record, handle, ct).ConfigureAwait(false);
            default:
                var exported = await ExportAsync(handle, record, ct).ConfigureAwait(false);
                return exported.IsFailure
                    ? Result<(SandboxRecord, string?), AgentError>.Failure(exported.Error)
                    : Result<(SandboxRecord, string?), AgentError>.Success((record, null));
        }
    }

    /// <summary>
    /// One restart attempt: counted in <see cref="SandboxRecord.ExportAttempts"/>, and marked
    /// <see cref="SandboxRecord.RestartedByPark"/>, before it is made, so a park that dies midway counts too and no record
    /// stays exporting forever; then the sandbox is started again and exported. An attempt the caller cancels, such as a
    /// sweep whose park budget ran out, is not the sandbox's failure: its count is given back, the mark is kept.
    /// </summary>
    private async Task<Result<(SandboxRecord, string?), AgentError>> RestartAndExportAsync(SandboxRecord record, SandboxHandle handle, CancellationToken ct)
    {
        var counted = record with { ExportAttempts = record.ExportAttempts + 1, RestartedByPark = true };
        var written = await _store.WriteAsync(counted, ct).ConfigureAwait(false);
        if (written.IsFailure)
        {
            return Result<(SandboxRecord, string?), AgentError>.Failure(written.Error);
        }

        try
        {
            var restarted = await RestartAsync(counted, handle, ct).ConfigureAwait(false);
            if (restarted.IsFailure)
            {
                return Result<(SandboxRecord, string?), AgentError>.Failure(restarted.Error);
            }

            var exported = await ExportAsync(restarted.Value, counted, ct).ConfigureAwait(false);
            return exported.IsFailure
                ? Result<(SandboxRecord, string?), AgentError>.Failure(exported.Error)
                : Result<(SandboxRecord, string?), AgentError>.Success((counted, null));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var refunded = await _store.WriteAsync(counted with { ExportAttempts = record.ExportAttempts }, CancellationToken.None).ConfigureAwait(false);
            if (refunded.IsFailure)
            {
                LogCleanupFailed(logger, $"give back the cancelled restart attempt of run '{record.RunId}'", refunded.Error.Message);
            }

            throw;
        }
    }

    /// <summary>
    /// Starts the sandbox again unless it runs, then waits, up to <see cref="SandboxOptions.RestartTimeout"/>, until its
    /// host answers <c>/control/ready</c>. Returns the running sandbox. Nothing on its volume is read from the host.
    /// </summary>
    private async Task<Result<SandboxHandle, AgentError>> RestartAsync(SandboxRecord record, SandboxHandle handle, CancellationToken ct)
    {
        if (handle.State != SandboxState.Running)
        {
            var started = await runtime.StartAsync(record.SandboxId, ct).ConfigureAwait(false);
            if (started.IsFailure)
            {
                return Result<SandboxHandle, AgentError>.Failure(started.Error);
            }
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(options.RestartTimeout);
        try
        {
            while (true)
            {
                if (await runtime.GetAsync(record.SandboxId, bounded.Token).ConfigureAwait(false) is { State: SandboxState.Running } running
                    && running.RunId == record.RunId
                    && (await control.ReadyAsync(running, record.Token, bounded.Token).ConfigureAwait(false)).IsSuccess)
                {
                    return Result<SandboxHandle, AgentError>.Success(running);
                }

                await Task.Delay(ReadyPollInterval, clock, bounded.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result<SandboxHandle, AgentError>.Failure(AgentError.ProviderError(
                $"The sandbox of run '{record.RunId}' did not answer within {options.RestartTimeout} of being started again."));
        }
    }

    /// <summary>
    /// Streams the sandbox's patch to the run's temp path under <see cref="SandboxOptions.ExportTimeout"/>, then moves it
    /// over the run's stored patch. A failure leaves neither a temp file nor a new patch.
    /// </summary>
    private async Task<UnitResult<AgentError>> ExportAsync(SandboxHandle handle, SandboxRecord record, CancellationToken ct)
    {
        var temp = _store.PatchTempPath(record.RunId);

        // Left by a park that died mid-export; the export refuses to write over an existing file.
        if (!_store.DeleteFileIfPresent(temp, "delete an earlier park's partial patch"))
        {
            return UnitResult<AgentError>.Failure(AgentError.StoreError($"Could not delete the partial patch of run '{record.RunId}'."));
        }

        using (var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            bounded.CancelAfter(options.ExportTimeout);
            try
            {
                var exported = await control.ExportToFileAsync(handle, record.Token, record.BaseCommit, temp, options.PatchLimits.MaxPatchBytes, bounded.Token).ConfigureAwait(false);
                if (exported.IsFailure)
                {
                    return exported;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _store.DeleteFileIfPresent(temp, "delete a timed-out export's partial patch");
                return UnitResult<AgentError>.Failure(AgentError.ProviderError($"The sandbox's export did not finish within {options.ExportTimeout}."));
            }
        }

        try
        {
            File.Move(temp, _store.PatchPath(record.RunId), overwrite: true);
            return UnitResult<AgentError>.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _store.DeleteFileIfPresent(temp, "delete an exported patch that could not be stored");
            return UnitResult<AgentError>.Failure(AgentError.StoreError($"Could not store the exported patch of run '{record.RunId}'.", ex.Message));
        }
    }

    private async Task<Result<RunWorkspace, AgentError>> CheckoutLockedAsync(Guid runId, CancellationToken ct)
    {
        var parked = await ParkLockedAsync(runId, deadline: null, ct).ConfigureAwait(false);
        if (parked.IsFailure)
        {
            return Result<RunWorkspace, AgentError>.Failure(parked.Error);
        }

        var read = await ReadPublishableAsync(runId, ct).ConfigureAwait(false);
        if (read.IsFailure)
        {
            return Result<RunWorkspace, AgentError>.Failure(read.Error);
        }

        var record = read.Value;
        var existing = await publishWorktrees.FindAsync(runId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (record.PatchApplied)
            {
                return Result<RunWorkspace, AgentError>.Success(existing);
            }

            // Made by a checkout that never finished applying: it may hold the bare base, which must never be published
            // as the run's changes.
            var stale = await publishWorktrees.RemoveAsync(runId, ct).ConfigureAwait(false);
            if (stale.IsFailure)
            {
                return Result<RunWorkspace, AgentError>.Failure(stale.Error);
            }
        }

        var applied = await ApplyToNewWorktreeAsync(record, ct).ConfigureAwait(false);
        if (applied.IsFailure)
        {
            return applied;
        }

        var marked = await _store.WriteAsync(record with { PatchApplied = true }, ct).ConfigureAwait(false);
        return marked.IsFailure ? Result<RunWorkspace, AgentError>.Failure(marked.Error) : applied;
    }

    /// <summary>
    /// The parked record, once it names a stored patch where <see cref="GitPatchApplier"/>'s precondition wants it: a
    /// path no sandbox can write, derived from the run id under the trusted state directory. A record that names any
    /// other path is refused rather than followed.
    /// </summary>
    private async Task<Result<SandboxRecord, AgentError>> ReadPublishableAsync(Guid runId, CancellationToken ct)
    {
        var read = await _store.ReadAsync(runId, ct).ConfigureAwait(false);
        if (read.Record is not { State: SandboxRecordState.Parked } record)
        {
            return Result<SandboxRecord, AgentError>.Failure(read.Error is { } error ? Unreadable(runId, error) : NothingToPark(runId, read.Record?.State));
        }

        if (record.PatchMissing)
        {
            return Result<SandboxRecord, AgentError>.Failure(AgentError.ProviderError(
                $"the run's sandbox was lost before its changes were exported: {record.PatchMissingReason ?? LostReason}"));
        }

        var patch = _store.PatchPath(runId);
        return string.Equals(record.PatchPath, patch, StringComparison.Ordinal)
            ? Result<SandboxRecord, AgentError>.Success(record)
            : Result<SandboxRecord, AgentError>.Failure(AgentError.Validation($"The sandbox record of run '{runId}' does not name its stored patch; publish refused."));
    }

    /// <summary>
    /// A new publish worktree cut from the record's base commit, with the stored patch applied. A refused patch removes
    /// the worktree again (S5).
    /// </summary>
    private async Task<Result<RunWorkspace, AgentError>> ApplyToNewWorktreeAsync(SandboxRecord record, CancellationToken ct)
    {
        var request = new RunWorkspaceRequest(record.RunId, record.Repository, record.Remote, record.DefaultBranch, record.Branch, record.Solution)
        {
            StartPoint = record.BaseCommit,
        };
        var created = await publishWorktrees.CreateAsync(request, ct).ConfigureAwait(false);
        if (created.IsFailure)
        {
            return created;
        }

        var applied = await patches.ApplyAsync(created.Value, _store.PatchPath(record.RunId), _protected, options.PatchLimits, ct).ConfigureAwait(false);
        if (applied.IsSuccess)
        {
            return created;
        }

        // Uncancelled, so a refused patch never leaves a worktree to publish; each git call is bounded on its own.
        var removed = await publishWorktrees.RemoveAsync(record.RunId, CancellationToken.None).ConfigureAwait(false);
        if (removed.IsFailure)
        {
            LogCleanupFailed(logger, $"remove the refused publish worktree of run '{record.RunId}'; the next checkout rebuilds it", removed.Error.Message);
        }

        return Result<RunWorkspace, AgentError>.Failure(applied.Error);
    }

    /// <summary>Waits, up to <see cref="RunLockWait"/>, for the run's lock.</summary>
    private async Task<Result<FileStream, AgentError>> LockRunAsync(Guid runId, CancellationToken ct)
    {
        // First, so the lock's own directory is never what creates the state directory with default permissions.
        if (_store.EnsureDirectory() is { } unusable)
        {
            return Result<FileStream, AgentError>.Failure(unusable);
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(RunLockWait);
        try
        {
            return Result<FileStream, AgentError>.Success(await CrossProcessFileLock.AcquireAsync(_store.LockPath(runId), bounded.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result<FileStream, AgentError>.Failure(AgentError.ProviderError(
                $"The sandbox of run '{runId}' was held by another call for longer than {RunLockWait}."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<FileStream, AgentError>.Failure(AgentError.StoreError($"Could not open the sandbox lock of run '{runId}'.", ex.Message));
        }
    }

    private static AgentError Unreadable(Guid runId, string error) =>
        AgentError.StoreError($"The sandbox record of run '{runId}' could not be read; leaving it for an operator.", error);

    private static AgentError NothingToPark(Guid runId, SandboxRecordState? state) =>
        AgentError.Validation(state is { } s
            ? $"Run '{runId}' has no sandbox to park: its record is {s.ToString().ToLowerInvariant()}."
            : $"Run '{runId}' has no sandbox.");
}
