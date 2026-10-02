using Microsoft.Extensions.Logging;
using Thalos.Git.Workspaces;
using Thalos.Mcp;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Sandbox;

/// <summary>
/// The trusted side's <see cref="IRunWorkspaceProvider"/> for sandboxed runs: each run's workspace, build and Roslyn
/// live in its own container, and this provider turns a run into one, mirror, bundle, container, import, record, and
/// later parks it and hands its changes off for publishing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The workspace of a sandboxed run is not a host directory.</b> <see cref="RunWorkspace.Root"/> is
/// <c>sandbox://&lt;SandboxId&gt;</c>. It is not a fully qualified host path, and <see cref="WorkspacePath.Resolve"/>
/// refuses every root that is not one, so no workspace tool on the host resolves a path under it. No host process may
/// be started in it either: <see cref="SandboxThalosBuilderExtensions.UseSandboxRunWorkspaces"/> refuses, when this
/// provider is built, any run-scoped MCP server that is not remote, since the host would spawn it with this root as
/// its working directory. <see cref="RunWorkspace.SolutionPath"/> is the solution relative to the repository root,
/// not an absolute path, and <see cref="RunWorkspace.BaseCommit"/> is the record's. The run's tools are served by the
/// sandbox, found through <see cref="ResolveAsync"/>; host code reads base files through
/// <see cref="ReadBaseFileAsync"/>, from the mirror, never from the sandbox.
/// </para>
/// <para>
/// <b>Trusted state.</b> Records, bundles, stored patches and run locks live under <c>&lt;DataRoot&gt;/sandboxes</c>,
/// owner-only on Unix; the mirror and the publish worktrees under <c>&lt;DataRoot&gt;/publish</c>. No container mounts
/// either. A record holds its sandbox's bearer token, 32 random bytes as base64url, new for every sandbox, so a run's
/// re-created sandbox never accepts its predecessor's token.
/// </para>
/// <para>
/// <b>One call per run at a time.</b> <see cref="CreateAsync"/>, <see cref="RemoveAsync"/>, <see cref="ParkAsync"/>
/// and <see cref="CheckoutForPublishAsync"/> hold the run's lock, <c>sandboxes/locks/&lt;run-id&gt;.lock</c>, for their
/// whole length. A create or remove refuses at once when another call holds it; a park or checkout waits for it, for a
/// bounded time.
/// The observers are told under that lock, so a run's <see cref="IRunWorkspaceObserver.OnReadyAsync"/> and
/// <see cref="IRunWorkspaceObserver.OnRemovingAsync"/> never overlap and arrive in order: a late removal notice can
/// never reach an observer after the run's next sandbox was announced ready. The claim itself is an atomic no-replace
/// publish of the record, so two creates for one run cannot both own it even across processes.
/// </para>
/// <para>
/// <b>A create undoes itself.</b> Every way out of <see cref="CreateAsync"/> after the claim that is not a ready
/// workspace, a failure, an exception or a cancellation, deletes the sandbox if this call created it, the bundle and
/// the record, uncancelled. A runtime failure is returned unchanged. A sandbox that cannot be deleted leaves its
/// record provisional, for <see cref="ReconcileAsync"/> to finish.
/// </para>
/// </remarks>
/// <param name="options">The sandbox options; <see cref="SandboxOptions.DataRoot"/> is absolute.</param>
/// <param name="runtime">Creates, finds and deletes the containers.</param>
/// <param name="mirrors">The mirror under <c>&lt;DataRoot&gt;/publish</c>, shared with <paramref name="publishWorktrees"/>.</param>
/// <param name="publishWorktrees">The trusted worktrees a run's patch is applied in for publishing.</param>
/// <param name="patches">Applies a stored patch to a publish worktree.</param>
/// <param name="observers">Told when a run's sandbox is ready and before it is removed.</param>
/// <param name="control">The sandbox host's control routes.</param>
/// <param name="logger">Logs cleanup failures, lost sandboxes and observer failures.</param>
/// <param name="clock">Stamps records and ages them.</param>
public sealed partial class SandboxRunWorkspaceProvider(
    SandboxOptions options, ISandboxRuntime runtime, GitMirrorStore mirrors, SandboxPublishWorktrees publishWorktrees,
    GitPatchApplier patches, IEnumerable<IRunWorkspaceObserver> observers, SandboxControlClient control,
    ILogger<SandboxRunWorkspaceProvider> logger, TimeProvider clock)
    : IRunWorkspaceProvider, IRunBaseFileReader, IRunToolEndpointResolver, IRunToolServerReadiness, IParkableRunWorkspaceProvider, IRunWorkspaceHandoff
{
    /// <summary>The scheme of a sandboxed run's <see cref="RunWorkspace.Root"/>.</summary>
    public const string RootScheme = "sandbox://";

    /// <summary>How old a provisional record with no live create must be before <see cref="ReconcileAsync"/> deletes it.</summary>
    internal static readonly TimeSpan ProvisionalGrace = TimeSpan.FromMinutes(10);

    private readonly SandboxRecordStore _store = new(options.DataRoot, clock, logger);
    private readonly ProtectedPathSet _protected = new(options.ProtectedPaths);

    /// <summary>How often <see cref="WaitAllReadyAsync"/> asks the sandbox. Two seconds; tests shorten it.</summary>
    internal TimeSpan ReadyPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The trusted state, for tests.</summary>
    internal SandboxRecordStore Store => _store;

    // ---------- find, list ----------

    /// <inheritdoc />
    /// <remarks>A ready, exporting or parked run's workspace; a provisional or removing one is not a workspace.</remarks>
    public async ValueTask<RunWorkspace?> FindAsync(Guid runId, CancellationToken ct)
    {
        var read = await _store.ReadAsync(runId, ct).ConfigureAwait(false);
        if (read.Error is { } error)
        {
            LogUnreadableRecord(logger, runId, error);
        }

        return read.Record is { State: SandboxRecordState.Ready or SandboxRecordState.Exporting or SandboxRecordState.Parked } record
            ? Workspace(record)
            : null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Every record but a removing one, each with its <see cref="RunWorkspace.CreatedAt"/>, so a sweeper sees a crashed
    /// create's provisional record too. An unreadable record is logged and skipped.
    /// </remarks>
    public async ValueTask<IReadOnlyList<RunWorkspace>> ListAsync(CancellationToken ct)
    {
        var workspaces = new List<RunWorkspace>();
        foreach (var read in await _store.ListAsync(ct).ConfigureAwait(false))
        {
            if (read.Error is { } error)
            {
                LogUnreadableRecord(logger, read.RunId, error);
            }
            else if (read.Record is { State: not SandboxRecordState.Removing } record)
            {
                workspaces.Add(Workspace(record));
            }
        }

        return workspaces;
    }

    // ---------- remove ----------

    /// <inheritdoc />
    /// <remarks>
    /// Marks the record removing, tells the observers, deletes the sandbox, the stored patch and the publish worktree,
    /// then the record. A failure leaves the record removing, which <see cref="ListAsync"/> no longer reports; a later
    /// call, or the next boot's <see cref="ReconcileAsync"/>, finishes it, telling the observers again. An absent record succeeds. Refused while another call holds the run's lock.
    /// </remarks>
    public async ValueTask<UnitResult<AgentError>> RemoveAsync(Guid runId, CancellationToken ct)
    {
        var runLock = await TryLockRunAsync(runId, ct).ConfigureAwait(false);
        if (runLock.IsFailure)
        {
            return UnitResult<AgentError>.Failure(runLock.Error);
        }

        if (runLock.Value is not { } held)
        {
            return UnitResult<AgentError>.Failure(Busy(runId));
        }

        var runGone = false;
        try
        {
            var removed = await RemoveLockedAsync(runId, ct).ConfigureAwait(false);
            runGone = removed.IsSuccess;
            return removed;
        }
        finally
        {
            ReleaseRunLock(held, runId, deleteFile: runGone);
        }
    }

    private async Task<UnitResult<AgentError>> RemoveLockedAsync(Guid runId, CancellationToken ct)
    {
        var read = await _store.ReadAsync(runId, ct).ConfigureAwait(false);
        if (read.Error is { } error)
        {
            return UnitResult<AgentError>.Failure(AgentError.StoreError($"The sandbox record of run '{runId}' could not be read; leaving it for an operator.", error));
        }

        if (read.Record is not { } record)
        {
            return UnitResult<AgentError>.Success();
        }

        // A provisional record here is a create that died: observers never heard of it.
        if (record.State != SandboxRecordState.Provisional)
        {
            if (record.State != SandboxRecordState.Removing)
            {
                var marked = await _store.WriteAsync(record with { State = SandboxRecordState.Removing }, ct).ConfigureAwait(false);
                if (marked.IsFailure)
                {
                    return marked;
                }
            }

            await NotifyObserversAsync(Workspace(record), removing: true, ct).ConfigureAwait(false);
        }

        var deleted = await runtime.DeleteAsync(record.SandboxId, ct).ConfigureAwait(false);
        if (deleted.IsFailure)
        {
            return deleted;
        }

        // The paths the run id implies, never a path read from the record.
        if (!_store.DeleteFileIfPresent(_store.PatchPath(runId), "delete a removed run's stored patch")
            || !_store.DeleteFileIfPresent(_store.PatchTempPath(runId), "delete a removed run's partial patch")
            || !_store.DeleteFileIfPresent(_store.BundlePath(runId), "delete a removed run's bundle"))
        {
            return UnitResult<AgentError>.Failure(AgentError.StoreError($"Could not delete the stored files of run '{runId}'."));
        }

        var unpublished = await publishWorktrees.RemoveAsync(runId, ct).ConfigureAwait(false);
        return unpublished.IsFailure ? unpublished : _store.Delete(runId);
    }

    // ---------- helpers ----------

    /// <summary>The workspace a record describes; see the class remarks for its <c>sandbox://</c> root.</summary>
    private static RunWorkspace Workspace(SandboxRecord record) =>
        new(record.RunId, record.Repository, record.Remote, record.DefaultBranch, record.Branch, RootScheme + record.SandboxId, record.Solution)
        {
            CreatedAt = record.CreatedAt,
            BaseCommit = record.BaseCommit,
        };

    private static AgentError Busy(Guid runId) =>
        AgentError.Validation($"The sandbox of run '{runId}' is being created or removed by another call.");

    private static AgentError AlreadyExists(Guid runId) =>
        AgentError.Validation($"Run '{runId}' already has a sandbox.");


    private async Task<Result<FileStream?, AgentError>> TryLockRunAsync(Guid runId, CancellationToken ct)
    {
        // First, so the lock's own directory is never what creates the state directory with default permissions.
        if (_store.EnsureDirectory() is { } unusable)
        {
            return Result<FileStream?, AgentError>.Failure(unusable);
        }

        try
        {
            return Result<FileStream?, AgentError>.Success(await CrossProcessFileLock.TryAcquireAsync(_store.LockPath(runId), ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<FileStream?, AgentError>.Failure(AgentError.StoreError($"Could not open the sandbox lock of run '{runId}'.", ex.Message));
        }
    }

    private void ReleaseRunLock(FileStream held, Guid runId, bool deleteFile)
    {
        if (!deleteFile)
        {
            held.Dispose();
            return;
        }

        try
        {
            CrossProcessFileLock.DeleteHeld(held, _store.LockPath(runId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCleanupFailed(logger, $"delete the sandbox lock of run '{runId}'", ex.Message);
        }
    }

    private async Task NotifyObserversAsync(RunWorkspace workspace, bool removing, CancellationToken ct)
    {
        foreach (var observer in observers)
        {
            try
            {
                if (removing)
                {
                    await observer.OnRemovingAsync(workspace, ct).ConfigureAwait(false);
                }
                else
                {
                    await observer.OnReadyAsync(workspace, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                LogObserverFailed(logger, workspace.RunId, removing ? nameof(IRunWorkspaceObserver.OnRemovingAsync) : nameof(IRunWorkspaceObserver.OnReadyAsync), ex.Message);
            }
        }
    }


    [LoggerMessage(EventId = 2101, Level = LogLevel.Warning, Message = "Could not {What}: {Error}")]
    private static partial void LogCleanupFailed(ILogger logger, string what, string error);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Warning, Message = "Sandbox record of run {RunId} could not be read and was skipped: {Error}")]
    private static partial void LogUnreadableRecord(ILogger logger, Guid runId, string error);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Warning, Message = "Workspace observer for run {RunId} failed during {Phase}: {Error}")]
    private static partial void LogObserverFailed(ILogger logger, Guid runId, string phase, string error);

    [LoggerMessage(EventId = 2104, Level = LogLevel.Warning, Message = "Resolving the sandbox endpoint of run {RunId} failed: {Error}")]
    private static partial void LogResolveFailed(ILogger logger, Guid runId, string error);

    [LoggerMessage(EventId = 2105, Level = LogLevel.Warning, Message = "The sandbox of run {RunId} failed to restore; its tools are served anyway: {Detail}")]
    private static partial void LogRestoreFailed(ILogger logger, Guid runId, string detail);

    [LoggerMessage(EventId = 2106, Level = LogLevel.Error, Message = "SandboxLost: run {RunId} is recorded ready but its sandbox {SandboxId} no longer exists; the record is kept")]
    private static partial void LogSandboxLost(ILogger logger, Guid runId, string sandboxId);

    [LoggerMessage(EventId = 2107, Level = LogLevel.Warning, Message = "The sandbox runtime listed no sandboxes while {Count} records expect one; it may be unreachable, so none is reported lost")]
    private static partial void LogRuntimeListedNothing(ILogger logger, int count);

    [LoggerMessage(EventId = 2108, Level = LogLevel.Information, Message = "Deleted sandbox {SandboxId}, which has no record")]
    private static partial void LogOrphanDeleted(ILogger logger, string sandboxId);

    [LoggerMessage(EventId = 2109, Level = LogLevel.Warning, Message = "Reconciling {Subject} failed and was skipped: {Error}")]
    private static partial void LogReconcileStepFailed(ILogger logger, string subject, string error);

    [LoggerMessage(EventId = 2110, Level = LogLevel.Error, Message = "SandboxLost: the sandbox {SandboxId} of run {RunId} was gone before its patch was exported; the run is parked with no patch to publish")]
    private static partial void LogLostBeforeExport(ILogger logger, Guid runId, string sandboxId);

    [LoggerMessage(EventId = 2111, Level = LogLevel.Error, Message = "SandboxLost: the exited sandbox {SandboxId} of run {RunId} failed {Attempts} restarts to export; the run is parked with no patch to publish")]
    private static partial void LogRestartsExhausted(ILogger logger, Guid runId, string sandboxId, int attempts);
}
