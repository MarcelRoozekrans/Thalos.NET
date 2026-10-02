using Thalos.Workspaces;

namespace Thalos.Sandbox;

public sealed partial class SandboxRunWorkspaceProvider
{
    // ---------- reconcile ----------

    /// <summary>
    /// Run once at boot by <see cref="SandboxReconcileService"/>: settles what a restart left behind. Returns how many
    /// sandboxes and records it deleted.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>A provisional record older than ten minutes whose create is not alive: its sandbox and record are deleted.</item>
    /// <item>A ready record whose container is missing is kept, and logged as lost.</item>
    /// <item>An exporting record is left for the next park; a parked one has no container.</item>
    /// <item>A removing record's removal is finished.</item>
    /// <item>A sandbox with no record is deleted once its <see cref="SandboxHandle.CreatedAt"/> is older than <see cref="RunWorkspaceGrace.Orphan"/>.</item>
    /// </list>
    /// <para>
    /// One record or sandbox that cannot be settled is logged and the pass goes on. A removal that failed earlier left its
    /// record removing; the next boot's reconcile finishes it.
    /// </para>
    /// <para>
    /// <b>An empty runtime list is not a verdict.</b> <see cref="ISandboxRuntime.ListAsync"/> answers empty when the
    /// engine cannot be asked, too. So when it lists nothing while records expect running containers, no ready record is
    /// reported lost; that is logged once instead. Nothing here deletes a record because its container was not listed:
    /// a record is only deleted after its sandbox's delete succeeded, which an unreachable engine fails.
    /// </para>
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask<int> ReconcileAsync(CancellationToken ct)
    {
        var records = await _store.ListAsync(ct).ConfigureAwait(false);
        var handles = await runtime.ListAsync(ct).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var expecting = records.Count(r => r.Record is { State: SandboxRecordState.Ready or SandboxRecordState.Exporting });
        var listed = handles.Count > 0 || expecting == 0;
        if (!listed)
        {
            LogRuntimeListedNothing(logger, expecting);
        }

        var live = handles.Select(h => h.SandboxId).ToHashSet(StringComparer.Ordinal);
        var deleted = 0;
        foreach (var read in records)
        {
            ct.ThrowIfCancellationRequested();
            if (read.Error is { } error)
            {
                LogUnreadableRecord(logger, read.RunId, error);
            }
            else if (read.Record is { } record)
            {
                // One record's failure is logged and the pass goes on to the next.
                try
                {
                    deleted += await ReconcileRecordAsync(record, listed ? live : null, now, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    LogReconcileStepFailed(logger, record.RunId.ToString(), ex.Message);
                }
            }
        }

        // An unreadable record still names its run, so its sandbox is never taken for an orphan: by the handle's run id,
        // or by its sandbox id, which is the run id in "N" format.
        var recorded = records.Select(r => r.RunId).ToHashSet();
        var recordedIds = records.Select(r => r.RunId.ToString("N")).ToHashSet(StringComparer.Ordinal);
        foreach (var handle in handles)
        {
            if (recorded.Contains(handle.RunId) || recordedIds.Contains(handle.SandboxId) || now - handle.CreatedAt <= RunWorkspaceGrace.Orphan)
            {
                continue;
            }

            try
            {
                deleted += await DeleteOrphanAsync(handle, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                LogReconcileStepFailed(logger, handle.SandboxId, ex.Message);
            }
        }

        return deleted;
    }

    private async Task<int> DeleteOrphanAsync(SandboxHandle handle, CancellationToken ct)
    {
        var gone = await runtime.DeleteAsync(handle.SandboxId, ct).ConfigureAwait(false);
        if (gone.IsFailure)
        {
            LogCleanupFailed(logger, $"delete the unrecorded sandbox '{handle.SandboxId}'", gone.Error.Message);
            return 0;
        }

        LogOrphanDeleted(logger, handle.SandboxId);
        return 1;
    }

    /// <summary>
    /// Settles one record; <paramref name="live"/> is null when the runtime's list cannot be trusted. Returns how many
    /// records it deleted.
    /// </summary>
    private async Task<int> ReconcileRecordAsync(SandboxRecord record, HashSet<string>? live, DateTimeOffset now, CancellationToken ct)
    {
        switch (record.State)
        {
            case SandboxRecordState.Provisional when now - record.CreatedAt > ProvisionalGrace:
                return await DeleteAbandonedAsync(record, ct).ConfigureAwait(false);
            case SandboxRecordState.Ready when live is not null && !live.Contains(record.SandboxId):
                LogSandboxLost(logger, record.RunId, record.SandboxId);
                return 0;
            case SandboxRecordState.Removing:
                var removed = await RemoveAsync(record.RunId, ct).ConfigureAwait(false);
                if (removed.IsFailure)
                {
                    LogCleanupFailed(logger, $"finish removing run '{record.RunId}'", removed.Error.Message);
                }

                return removed.IsSuccess ? 1 : 0;
            default:
                // Exporting is left for the next park; a parked run has no container.
                return 0;
        }
    }

    /// <summary>Deletes an abandoned provisional record's sandbox and then the record, unless its create is still alive.</summary>
    private async Task<int> DeleteAbandonedAsync(SandboxRecord record, CancellationToken ct)
    {
        var runLock = await TryLockRunAsync(record.RunId, ct).ConfigureAwait(false);
        if (runLock.IsFailure || runLock.Value is not { } held)
        {
            return 0;
        }

        var gone = false;
        try
        {
            var deleted = await runtime.DeleteAsync(record.SandboxId, ct).ConfigureAwait(false);
            if (deleted.IsFailure)
            {
                LogCleanupFailed(logger, $"delete the sandbox of abandoned run '{record.RunId}'", deleted.Error.Message);
                return 0;
            }

            _store.DeleteFileIfPresent(_store.BundlePath(record.RunId), "delete an abandoned create's bundle");
            var removed = _store.Delete(record.RunId);
            if (removed.IsFailure)
            {
                LogCleanupFailed(logger, $"delete the record of abandoned run '{record.RunId}'", removed.Error.Message);
                return 0;
            }

            gone = true;
            return 1;
        }
        finally
        {
            ReleaseRunLock(held, record.RunId, deleteFile: gone);
        }
    }
}
