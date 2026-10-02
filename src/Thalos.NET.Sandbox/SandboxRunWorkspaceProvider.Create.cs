using System.Buffers.Text;
using System.Security.Cryptography;
using Thalos.Git.Workspaces;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Sandbox;

public sealed partial class SandboxRunWorkspaceProvider
{
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
        var bundle = _store.BundlePath(record.RunId);
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

    /// <summary>
    /// Waits until the new sandbox's host answers, then streams the bundle to it, both under
    /// <see cref="SandboxOptions.ImportTimeout"/>. A container that was just started may not be listening yet; the
    /// gateway then answers 502, which is no answer from the host.
    /// </summary>
    private async Task<UnitResult<AgentError>> ImportAsync(SandboxHandle sandbox, SandboxRecord record, string bundle, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(options.ImportTimeout);
        try
        {
            var answering = await WaitAnsweringAsync(sandbox, record, bounded.Token).ConfigureAwait(false);
            if (answering.IsFailure)
            {
                return answering;
            }

            var stream = new FileStream(bundle, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                return await control.ImportAsync(sandbox, record.Token, stream, record.Branch, record.BaseCommit, record.Solution, bounded.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return UnitResult<AgentError>.Failure(AgentError.ProviderError($"The sandbox did not answer and take its import within {options.ImportTimeout}."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UnitResult<AgentError>.Failure(AgentError.StoreError($"Could not read the bundle '{bundle}'.", ex.Message));
        }
    }

    /// <summary>
    /// Asks the new sandbox every <see cref="ReadyPollInterval"/> until its host answers; fails at once when its container
    /// stopped. The caller's token bounds the wait.
    /// </summary>
    private async Task<UnitResult<AgentError>> WaitAnsweringAsync(SandboxHandle sandbox, SandboxRecord record, CancellationToken ct)
    {
        while (!await control.AnswersAsync(sandbox, record.Token, ct).ConfigureAwait(false))
        {
            if (await runtime.GetAsync(record.SandboxId, ct).ConfigureAwait(false) is { State: SandboxState.Exited or SandboxState.Missing } stopped)
            {
                return UnitResult<AgentError>.Failure(AgentError.ProviderError($"The run's sandbox did not start: {Stopped(stopped)}."));
            }

            await Task.Delay(ReadyPollInterval, clock, ct).ConfigureAwait(false);
        }

        return UnitResult<AgentError>.Success();
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

        _store.DeleteFileIfPresent(_store.BundlePath(record.RunId), "delete the bundle of an undone create");
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

    /// <summary>32 bytes from <see cref="RandomNumberGenerator"/>, as base64url: 43 characters.</summary>
    private static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>The request checks shared with <see cref="GitWorktreeWorkspaceProvider"/>, and a required solution.</summary>
    private static AgentError? Validate(RunWorkspaceRequest request)
    {
        if (RunWorkspaceRequestValidator.Validate(request) is { } invalid)
        {
            return invalid;
        }

        if (string.IsNullOrWhiteSpace(request.Solution))
        {
            return AgentError.Validation("A sandboxed run needs a Solution: the sandbox starts Roslyn on it.");
        }

        return null;
    }

    /// <summary>How far one create got after its claim, so its undo removes exactly what it made.</summary>
    private sealed class CreateProgress
    {
        /// <summary>The runtime was asked to create the sandbox and did not answer with a failure.</summary>
        public bool SandboxAttempted { get; set; }

        /// <summary>The record turned ready and observers are being told; they must hear of the removal too.</summary>
        public RunWorkspace? Ready { get; set; }
    }
}
