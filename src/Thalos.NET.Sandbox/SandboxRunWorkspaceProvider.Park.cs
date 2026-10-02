using Thalos.Git.Workspaces;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Sandbox;

public sealed partial class SandboxRunWorkspaceProvider
{
    // ---------- park, hand-off ----------

    /// <summary>
    /// How long a park or checkout waits for another call that holds the run's lock: another park, whose export is
    /// bounded by <see cref="SandboxOptions.ExportTimeout"/>, or a checkout, plus five minutes for their git work.
    /// </summary>
    private TimeSpan RunLockWait => options.ExportTimeout + TimeSpan.FromMinutes(5);

    /// <inheritdoc />
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
    /// </remarks>
    public async ValueTask<UnitResult<AgentError>> ParkAsync(Guid runId, CancellationToken ct)
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
            return await ParkLockedAsync(runId, ct).ConfigureAwait(false);
        }
        finally
        {
            ReleaseRunLock(runLock.Value, runId, deleteFile: false);
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
            ReleaseRunLock(runLock.Value, runId, deleteFile: false);
        }
    }

    private async Task<UnitResult<AgentError>> ParkLockedAsync(Guid runId, CancellationToken ct)
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

        var exporting = record with { State = SandboxRecordState.Exporting };
        if (record.State != SandboxRecordState.Exporting)
        {
            var marked = await _store.WriteAsync(exporting, ct).ConfigureAwait(false);
            if (marked.IsFailure)
            {
                return marked;
            }
        }

        var stored = await StorePatchAsync(record, handle.Value, ct).ConfigureAwait(false);
        if (stored.IsFailure)
        {
            return UnitResult<AgentError>.Failure(stored.Error);
        }

        var deleted = await runtime.DeleteAsync(record.SandboxId, ct).ConfigureAwait(false);
        if (deleted.IsFailure)
        {
            return deleted;
        }

        await NotifyObserversAsync(Workspace(exporting), removing: true, ct).ConfigureAwait(false);
        return await _store.WriteAsync(
            exporting with { State = SandboxRecordState.Parked, PatchPath = stored.Value ? _store.PatchPath(runId) : null, PatchMissing = !stored.Value },
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

    /// <summary>
    /// Exports the sandbox's patch to the run's stored patch or, for a missing sandbox, settles whether one is stored
    /// already. Reports whether the run has a stored patch.
    /// </summary>
    private async Task<Result<bool, AgentError>> StorePatchAsync(SandboxRecord record, SandboxHandle handle, CancellationToken ct)
    {
        if (handle.State != SandboxState.Missing)
        {
            var exported = await ExportAsync(handle, record, ct).ConfigureAwait(false);
            return exported.IsFailure ? Result<bool, AgentError>.Failure(exported.Error) : Result<bool, AgentError>.Success(true);
        }

        // A stored patch under an exporting record is a finished export whose park stopped before the record was
        // written; a ready record has none, since a remove deletes a run's patch before its record.
        var stored = record.State == SandboxRecordState.Exporting && File.Exists(_store.PatchPath(record.RunId));
        if (!stored)
        {
            LogLostBeforeExport(logger, record.RunId, record.SandboxId);
        }

        return Result<bool, AgentError>.Success(stored);
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
                var exported = await control.ExportToFileAsync(handle, record.Token, temp, options.PatchLimits.MaxPatchBytes, bounded.Token).ConfigureAwait(false);
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
        var parked = await ParkLockedAsync(runId, ct).ConfigureAwait(false);
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
            return Result<SandboxRecord, AgentError>.Failure(AgentError.ProviderError("the run's sandbox was lost before its changes were exported"));
        }

        var patch = _store.PatchPath(runId);
        return string.Equals(record.PatchPath, patch, StringComparison.Ordinal)
            && string.Equals(Path.GetDirectoryName(patch), _store.Directory, StringComparison.Ordinal)
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
            return Result<FileStream, AgentError>.Failure(AgentError.Validation(
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
