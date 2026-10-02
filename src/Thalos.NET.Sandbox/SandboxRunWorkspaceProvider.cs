using System.Buffers.Text;
using System.Security.Cryptography;
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
/// <c>sandbox://&lt;SandboxId&gt;</c>, which <see cref="WorkspacePath.Resolve"/> refuses on the <c>:</c>, so no host code
/// can mistake it for a path. <see cref="RunWorkspace.SolutionPath"/> is the solution relative to the repository root,
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
/// <b>One call per run at a time.</b> <see cref="CreateAsync"/> and <see cref="RemoveAsync"/> hold the run's lock,
/// <c>sandboxes/locks/&lt;run-id&gt;.lock</c>, for their whole length, and refuse at once when another call holds it.
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

    /// <summary>
    /// How old a sandbox with no record must be before <see cref="ReconcileAsync"/> deletes it. The same ten minutes as
    /// <c>RunWorkspaceSweeper.OrphanGrace</c>, which this package cannot reference.
    /// </summary>
    internal static readonly TimeSpan OrphanGrace = TimeSpan.FromMinutes(10);

    private readonly SandboxRecordStore _store = new(options.DataRoot, clock, logger);
    private readonly ProtectedPathSet _protected = new(options.ProtectedPaths);

    /// <summary>How often <see cref="WaitAllReadyAsync"/> asks the sandbox. Two seconds; tests shorten it.</summary>
    internal TimeSpan ReadyPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Where the trusted state lives, for A11's park and hand-off.</summary>
    internal SandboxRecordStore Store => _store;

    /// <summary>Applies a run's stored patch for publishing; used from A11.</summary>
    internal GitPatchApplier Patches => patches;

    // ---------- create ----------

    /// <inheritdoc />
    /// <remarks>
    /// Mirrors the repository, bundles the base commit, creates the sandbox, imports the bundle under
    /// <see cref="SandboxOptions.ImportTimeout"/>, then records the run ready and tells the observers. The base is
    /// <see cref="RunWorkspaceRequest.StartPoint"/>, or the default branch's tip in the freshly fetched mirror. A
    /// sandboxed run needs a <see cref="RunWorkspaceRequest.Solution"/>: the sandbox starts Roslyn on it.
    /// </remarks>
    public async ValueTask<Result<RunWorkspace, AgentError>> CreateAsync(RunWorkspaceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Validate(request) is { } invalid)
        {
            return Result<RunWorkspace, AgentError>.Failure(invalid);
        }

        var spec = new SandboxSpec
        {
            RunId = request.RunId,
            Image = options.Image,
            Token = NewToken(),
            AllowedWriteExtensions = options.AllowedWriteExtensions,
            ProtectedPaths = _protected,
            Limits = options.Limits,
        };
        var specValid = spec.Validate();
        if (specValid.IsFailure)
        {
            return Result<RunWorkspace, AgentError>.Failure(specValid.Error);
        }

        var runLock = await TryLockRunAsync(request.RunId, ct).ConfigureAwait(false);
        if (runLock.IsFailure)
        {
            return Result<RunWorkspace, AgentError>.Failure(runLock.Error);
        }

        if (runLock.Value is not { } held)
        {
            return Result<RunWorkspace, AgentError>.Failure(Busy(request.RunId));
        }

        var outcome = (Result: Result<RunWorkspace, AgentError>.Failure(Busy(request.RunId)), RunGone: false);
        try
        {
            outcome = await CreateLockedAsync(request, spec, ct).ConfigureAwait(false);
            return outcome.Result;
        }
        finally
        {
            ReleaseRunLock(held, request.RunId, deleteFile: outcome.RunGone);
        }
    }

    /// <summary>
    /// <see cref="CreateAsync"/> once it holds the run's lock: mirrors, claims the record, and creates. Reports whether
    /// the run is gone again, after an undo, so the lock file can go with it.
    /// </summary>
    private async Task<(Result<RunWorkspace, AgentError> Result, bool RunGone)> CreateLockedAsync(RunWorkspaceRequest request, SandboxSpec spec, CancellationToken ct)
    {
        var existing = await _store.ReadAsync(request.RunId, ct).ConfigureAwait(false);
        if (existing.Record is not null || existing.Error is not null)
        {
            return (Result<RunWorkspace, AgentError>.Failure(AlreadyExists(request.RunId)), false);
        }

        var mirror = await mirrors.PrepareAsync(request.Repository, request.Remote, ct).ConfigureAwait(false);
        if (mirror.IsFailure)
        {
            return (Result<RunWorkspace, AgentError>.Failure(mirror.Error), false);
        }

        var baseCommit = request.StartPoint;
        if (baseCommit is null)
        {
            var resolved = await mirrors.ResolveBranchAsync(mirror.Value, request.DefaultBranch, ct).ConfigureAwait(false);
            if (resolved.IsFailure)
            {
                return (Result<RunWorkspace, AgentError>.Failure(resolved.Error), false);
            }

            baseCommit = resolved.Value;
        }

        var record = new SandboxRecord(
            request.RunId, request.Repository, request.Remote, request.DefaultBranch, request.Branch, request.Solution,
            baseCommit, spec.SandboxId, spec.Token, SandboxRecordState.Provisional, clock.GetUtcNow());
        var claimed = await _store.TryClaimAsync(record, ct).ConfigureAwait(false);
        if (claimed.IsFailure || !claimed.Value)
        {
            return (Result<RunWorkspace, AgentError>.Failure(claimed.IsFailure ? claimed.Error : AlreadyExists(request.RunId)), false);
        }

        // From here this call owns the record: every way out that is not a ready workspace, a failure, an exception or
        // a cancellation, runs the same undo.
        var progress = new CreateProgress();
        Result<RunWorkspace, AgentError> created;
        try
        {
            created = await CreateClaimedAsync(mirror.Value, record, spec, progress, ct).ConfigureAwait(false);
        }
        catch
        {
            await UndoCreateAsync(record, progress).ConfigureAwait(false);
            throw;
        }

        return created.IsSuccess
            ? (created, false)
            : (created, await UndoCreateAsync(record, progress).ConfigureAwait(false));
    }

    private async Task<Result<RunWorkspace, AgentError>> CreateClaimedAsync(GitMirror mirror, SandboxRecord record, SandboxSpec spec, CreateProgress progress, CancellationToken ct)
    {
        var bundle = _store.BundlePath(record.SandboxId);
        var bundled = await mirrors.CreateBundleAsync(mirror, record.BaseCommit, bundle, ct).ConfigureAwait(false);
        if (bundled.IsFailure)
        {
            return Result<RunWorkspace, AgentError>.Failure(bundled.Error);
        }

        // Set before the call: a cancellation while it runs may leave a container, and with the run's record claimed
        // and its lock held, any container of this id is this call's own.
        progress.SandboxAttempted = true;
        var created = await runtime.CreateAsync(spec, ct).ConfigureAwait(false);
        if (created.IsFailure)
        {
            // A failed create touched no container, possibly another's, so the undo deletes none.
            progress.SandboxAttempted = false;
            return Result<RunWorkspace, AgentError>.Failure(created.Error);
        }

        var imported = await ImportAsync(created.Value, record, bundle, ct).ConfigureAwait(false);
        if (imported.IsFailure)
        {
            return Result<RunWorkspace, AgentError>.Failure(imported.Error);
        }

        _store.DeleteFileIfPresent(bundle, "delete an imported bundle");
        var ready = record with { State = SandboxRecordState.Ready, CreatedAt = clock.GetUtcNow() };
        var written = await _store.WriteAsync(ready, ct).ConfigureAwait(false);
        if (written.IsFailure)
        {
            return Result<RunWorkspace, AgentError>.Failure(written.Error);
        }

        var workspace = Workspace(ready);
        progress.Ready = workspace;
        await NotifyObserversAsync(workspace, removing: false, ct).ConfigureAwait(false);
        return Result<RunWorkspace, AgentError>.Success(workspace);
    }

    /// <summary>Streams the bundle to the sandbox under <see cref="SandboxOptions.ImportTimeout"/>.</summary>
    private async Task<UnitResult<AgentError>> ImportAsync(SandboxHandle sandbox, SandboxRecord record, string bundle, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(options.ImportTimeout);
        try
        {
            var stream = new FileStream(bundle, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                return await control.ImportAsync(sandbox, record.Token, stream, record.Branch, record.BaseCommit, record.Solution, bounded.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return UnitResult<AgentError>.Failure(AgentError.ProviderError($"The sandbox's import did not finish within {options.ImportTimeout}."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UnitResult<AgentError>.Failure(AgentError.StoreError($"Could not read the bundle '{bundle}'.", ex.Message));
        }
    }

    /// <summary>
    /// Undoes an incomplete create, uncancelled: tells the observers of a removal if they heard of a ready workspace,
    /// deletes the sandbox if this call created it, then the bundle and the record. A sandbox that cannot be deleted
    /// leaves the record, provisional or removing, for a later remove or <see cref="ReconcileAsync"/>. Returns whether the
    /// record is gone.
    /// </summary>
    private async Task<bool> UndoCreateAsync(SandboxRecord record, CreateProgress progress)
    {
        if (progress.Ready is { } ready)
        {
            var marked = await _store.WriteAsync(record with { State = SandboxRecordState.Removing, CreatedAt = ready.CreatedAt }, CancellationToken.None).ConfigureAwait(false);
            if (marked.IsFailure)
            {
                LogCleanupFailed(logger, "mark an undone create's record as removing", marked.Error.Message);
            }

            await NotifyObserversAsync(ready, removing: true, CancellationToken.None).ConfigureAwait(false);
        }

        _store.DeleteFileIfPresent(_store.BundlePath(record.SandboxId), "delete the bundle of an undone create");
        if (progress.SandboxAttempted)
        {
            var deleted = await runtime.DeleteAsync(record.SandboxId, CancellationToken.None).ConfigureAwait(false);
            if (deleted.IsFailure)
            {
                LogCleanupFailed(logger, $"delete the sandbox of an undone create for run '{record.RunId}'; its record is left for a later remove", deleted.Error.Message);
                return false;
            }
        }

        var gone = _store.Delete(record.RunId);
        if (gone.IsFailure)
        {
            LogCleanupFailed(logger, "delete the record of an undone create", gone.Error.Message);
            return false;
        }

        return true;
    }

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

    // ---------- tools ----------

    /// <inheritdoc />
    /// <remarks>
    /// Only for a ready record whose container is running; null otherwise, and null on any failure, which is logged:
    /// this never throws.
    /// </remarks>
    public async ValueTask<RunToolEndpoint?> ResolveAsync(Guid runId, string source, CancellationToken ct)
    {
        try
        {
            var read = await _store.ReadAsync(runId, ct).ConfigureAwait(false);
            if (read.Record is not { State: SandboxRecordState.Ready } record)
            {
                return null;
            }

            var handle = await runtime.GetAsync(record.SandboxId, ct).ConfigureAwait(false);
            return handle is { State: SandboxState.Running }
                ? new RunToolEndpoint(new Uri(handle.BaseAddress, $"mcp/{Uri.EscapeDataString(source)}"), record.Token)
                : null;
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
            {
                LogResolveFailed(logger, runId, ex.Message);
            }

            return null;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Asks the sandbox every two seconds. Succeeds once the import is done and Roslyn is ready; fails at once when
    /// Roslyn failed, with its detail, or when the container exited, with its exit code and whether it was killed for
    /// memory; fails when <paramref name="timeout"/> passes. A failed restore does not fail the wait: it is logged, and
    /// <see cref="ReadinessAsync"/> reports it.
    /// </remarks>
    public async ValueTask<UnitResult<AgentError>> WaitAllReadyAsync(Guid runId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = clock.GetUtcNow() + timeout;
        var last = "the sandbox has not answered yet";
        while (true)
        {
            var read = await _store.ReadAsync(runId, ct).ConfigureAwait(false);
            if (read.Record is not { State: SandboxRecordState.Ready } record)
            {
                return UnitResult<AgentError>.Failure(AgentError.Validation($"Run '{runId}' has no ready sandbox."));
            }

            var handle = await runtime.GetAsync(record.SandboxId, ct).ConfigureAwait(false);
            if (handle is { State: SandboxState.Exited or SandboxState.Missing } stopped)
            {
                return UnitResult<AgentError>.Failure(AgentError.ProviderError(Stopped(stopped)));
            }

            if (handle is null)
            {
                // Null is also how the runtime answers when the engine cannot be asked, so it is not yet a verdict.
                last = "the sandbox could not be found";
            }
            else
            {
                var ready = await control.ReadyAsync(handle, record.Token, ct).ConfigureAwait(false);
                if (ready.IsSuccess)
                {
                    var readiness = ready.Value;
                    if (string.Equals(readiness.Roslyn, "failed", StringComparison.Ordinal))
                    {
                        return UnitResult<AgentError>.Failure(AgentError.ProviderError(
                            "The run's sandbox failed to start Roslyn.", LogSanitizer.Clean(readiness.Detail, 2000)));
                    }

                    if (readiness.Imported && string.Equals(readiness.Roslyn, "ready", StringComparison.Ordinal))
                    {
                        if (string.Equals(readiness.Restore, "failed", StringComparison.Ordinal))
                        {
                            LogRestoreFailed(logger, runId, LogSanitizer.Clean(readiness.RestoreDetail));
                        }

                        return UnitResult<AgentError>.Success();
                    }

                    last = $"import {(readiness.Imported ? "done" : "pending")}, restore {LogSanitizer.Clean(readiness.Restore, 16)}, roslyn {LogSanitizer.Clean(readiness.Roslyn, 16)}";
                }
                else
                {
                    last = ready.Error.Message;
                }
            }

            var remaining = deadline - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return UnitResult<AgentError>.Failure(AgentError.ProviderError($"The run's sandbox was not ready within {timeout}.", last));
            }

            await Task.Delay(remaining < ReadyPollInterval ? remaining : ReadyPollInterval, clock, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The run's sandbox's import, restore and Roslyn state, so a host can record a restore failure. Fails when the run
    /// has no ready sandbox, when its container is not running, or when the sandbox cannot be asked.
    /// </summary>
    /// <param name="runId">The run.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask<Result<SandboxReadiness, AgentError>> ReadinessAsync(Guid runId, CancellationToken ct)
    {
        var read = await _store.ReadAsync(runId, ct).ConfigureAwait(false);
        if (read.Record is not { State: SandboxRecordState.Ready } record)
        {
            return Result<SandboxReadiness, AgentError>.Failure(AgentError.Validation($"Run '{runId}' has no ready sandbox."));
        }

        var handle = await runtime.GetAsync(record.SandboxId, ct).ConfigureAwait(false);
        return handle switch
        {
            null => Result<SandboxReadiness, AgentError>.Failure(AgentError.ProviderError("The run's sandbox could not be found.")),
            { State: SandboxState.Running } => await control.ReadyAsync(handle, record.Token, ct).ConfigureAwait(false),
            _ => Result<SandboxReadiness, AgentError>.Failure(AgentError.ProviderError(Stopped(handle))),
        };
    }

    /// <inheritdoc />
    /// <remarks>Reads the mirror at the record's base commit, never the sandbox, whatever the sandbox did to its copy.</remarks>
    public async ValueTask<Result<string?, AgentError>> ReadBaseFileAsync(Guid runId, string relativePath, CancellationToken ct)
    {
        var read = await _store.ReadAsync(runId, ct).ConfigureAwait(false);
        if (read.Record is not { State: SandboxRecordState.Ready or SandboxRecordState.Exporting or SandboxRecordState.Parked } record)
        {
            return Result<string?, AgentError>.Failure(AgentError.Validation($"Run '{runId}' has no workspace."));
        }

        if (!GitMirrorStore.IsValidRepositoryName(record.Repository))
        {
            return Result<string?, AgentError>.Failure(AgentError.Validation(
                $"The sandbox record of run '{runId}' names repository '{record.Repository}', which is not a valid mirror directory name."));
        }

        return await mirrors.ReadFileAsync(new GitMirror(record.Repository, mirrors.MirrorPath(record.Repository)), record.BaseCommit, relativePath, ct).ConfigureAwait(false);
    }

    // ---------- remove ----------

    /// <inheritdoc />
    /// <remarks>
    /// Marks the record removing, tells the observers, deletes the sandbox, the stored patch and the publish worktree,
    /// then the record. A failure leaves the record removing; a later call, or <see cref="ReconcileAsync"/>, finishes it,
    /// telling the observers again. An absent record succeeds. Refused while another call holds the run's lock.
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
            || !_store.DeleteFileIfPresent(_store.BundlePath(record.SandboxId), "delete a removed run's bundle"))
        {
            return UnitResult<AgentError>.Failure(AgentError.StoreError($"Could not delete the stored files of run '{runId}'."));
        }

        var unpublished = await publishWorktrees.RemoveAsync(runId, ct).ConfigureAwait(false);
        return unpublished.IsFailure ? unpublished : _store.Delete(runId);
    }

    // ---------- park, hand-off ----------

    /// <inheritdoc />
    public ValueTask<UnitResult<AgentError>> ParkAsync(Guid runId, CancellationToken ct) =>
        ValueTask.FromResult(UnitResult<AgentError>.Failure(AgentError.ProviderError("not implemented until A11")));

    /// <inheritdoc />
    public ValueTask<Result<RunWorkspace, AgentError>> CheckoutForPublishAsync(Guid runId, CancellationToken ct) =>
        ValueTask.FromResult(Result<RunWorkspace, AgentError>.Failure(AgentError.ProviderError("not implemented until A11")));

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
    /// <item>A sandbox with no record is deleted once its <see cref="SandboxHandle.CreatedAt"/> is older than ten minutes.</item>
    /// </list>
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
                deleted += await ReconcileRecordAsync(record, listed ? live : null, now, ct).ConfigureAwait(false);
            }
        }

        // An unreadable record still names its run, so its sandbox is never taken for an orphan.
        var recorded = records.Select(r => r.RunId).ToHashSet();
        foreach (var handle in handles)
        {
            if (recorded.Contains(handle.RunId) || now - handle.CreatedAt <= OrphanGrace)
            {
                continue;
            }

            var gone = await runtime.DeleteAsync(handle.SandboxId, ct).ConfigureAwait(false);
            if (gone.IsSuccess)
            {
                LogOrphanDeleted(logger, handle.SandboxId);
                deleted++;
            }
            else
            {
                LogCleanupFailed(logger, $"delete the unrecorded sandbox '{handle.SandboxId}'", gone.Error.Message);
            }
        }

        return deleted;
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

            _store.DeleteFileIfPresent(_store.BundlePath(record.SandboxId), "delete an abandoned create's bundle");
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

    // ---------- helpers ----------

    /// <summary>The workspace a record describes; see the class remarks for its <c>sandbox://</c> root.</summary>
    private static RunWorkspace Workspace(SandboxRecord record) =>
        new(record.RunId, record.Repository, record.Remote, record.DefaultBranch, record.Branch, RootScheme + record.SandboxId, record.Solution)
        {
            CreatedAt = record.CreatedAt,
            BaseCommit = record.BaseCommit,
        };

    /// <summary>32 bytes from <see cref="RandomNumberGenerator"/>, as base64url: 43 characters.</summary>
    private static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private static string Stopped(SandboxHandle handle) => handle.State == SandboxState.Missing
        ? "the run's sandbox is gone"
        : $"the run's sandbox stopped (exit {handle.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}{(handle.OomKilled ? ", killed for memory" : "")})";

    private static AgentError Busy(Guid runId) =>
        AgentError.Validation($"The sandbox of run '{runId}' is being created or removed by another call.");

    private static AgentError AlreadyExists(Guid runId) =>
        AgentError.Validation($"Run '{runId}' already has a sandbox.");

    /// <summary>The request checks of <see cref="GitWorktreeWorkspaceProvider"/>, and a required solution.</summary>
    private static AgentError? Validate(RunWorkspaceRequest request)
    {
        if (!GitMirrorStore.IsValidRepositoryName(request.Repository))
        {
            return AgentError.Validation($"Repository '{request.Repository}' is not a valid mirror directory name.");
        }

        if (string.IsNullOrWhiteSpace(request.Remote) || request.Remote.StartsWith('-'))
        {
            return AgentError.Validation($"Remote '{request.Remote}' must not be blank or start with '-'.");
        }

        if (string.IsNullOrWhiteSpace(request.DefaultBranch))
        {
            return AgentError.Validation("DefaultBranch must not be blank.");
        }

        if (string.IsNullOrWhiteSpace(request.Branch) || request.Branch.StartsWith('-'))
        {
            return AgentError.Validation($"Branch '{request.Branch}' must not be blank or start with '-'.");
        }

        if (request.StartPoint is not null && !GitMirrorStore.IsFullSha(request.StartPoint))
        {
            return AgentError.Validation("StartPoint must be a full 40-character commit sha.");
        }

        if (string.IsNullOrWhiteSpace(request.Solution))
        {
            return AgentError.Validation("A sandboxed run needs a Solution: the sandbox starts Roslyn on it.");
        }

        return null;
    }

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

    /// <summary>How far one create got after its claim, so its undo removes exactly what it made.</summary>
    private sealed class CreateProgress
    {
        /// <summary>The runtime was asked to create the sandbox and did not answer with a failure.</summary>
        public bool SandboxAttempted { get; set; }

        /// <summary>The record turned ready and observers are being told; they must hear of the removal too.</summary>
        public RunWorkspace? Ready { get; set; }
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
}
