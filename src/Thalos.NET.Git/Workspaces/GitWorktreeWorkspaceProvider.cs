using System.Text.Json;
using Microsoft.Extensions.Logging;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Git.Workspaces;

/// <summary>
/// <see cref="IRunWorkspaceProvider"/> backed by a host-managed bare git mirror per repository and one git worktree
/// per run, both driven entirely through <see cref="GitCli"/> — a real <c>git</c> child process, never LibGit2Sharp
/// (ruling R23).
/// </summary>
/// <remarks>
/// <para>
/// <b>Symlinks are always disabled.</b> Every worktree this provider creates has <c>core.symlinks=false</c> in
/// force before any file is checked out into it: set on the mirror's own config right after cloning, and passed
/// again as <c>-c core.symlinks=false</c> on the <c>git worktree add</c> call that actually performs the checkout,
/// so the setting is in effect regardless of which config layer a particular git version consults for that
/// operation. With symlinks off, a symlink committed to the repository is written into the workspace as a plain
/// text file containing its target path — Git for Windows' own default behavior — never as a filesystem link. This
/// matters because the workspace is written into by an unsupervised agent through <see cref="WorkspacePath.Resolve"/>:
/// a real symlink checked out from repository content could point through a host directory and back into the
/// workspace, and <see cref="WorkspacePath.Resolve"/>'s success or refusal on such a path would tell the agent
/// whether that host path exists. Disabling symlinks at the source closes that channel for every workspace this
/// provider creates; see <see cref="WorkspacePath.Resolve"/>'s own remarks for the position on a workspace some
/// other mechanism populated.
/// </para>
/// <para>
/// <b>The mirror is isolated from host git configuration and never trusts a stale <c>origin</c>.</b> Every git call
/// goes through <see cref="GitCli"/>, which ignores the host's system and user gitconfig entirely (see its own
/// remarks) — a global hook or a permissive <c>protocol.*.allow</c> on the machine this runs on cannot affect a
/// checkout this provider performs. Before every fetch, <c>remote.origin.url</c> is set to
/// <see cref="RunWorkspaceRequest.Remote"/>, so a mirror that was first cloned for one remote and is later reused
/// under a changed configuration always fetches from, and sends credentials to, the remote the current request
/// names — never a stale one left over from the mirror's first clone.
/// </para>
/// <para>
/// <b>A first clone is atomic, and losing a first-clone race is not a failure.</b> The mirror is cloned into a
/// temporary directory beside its final location and moved into place only once the clone and its follow-up config
/// calls all succeed; a failure at any point leaves nothing at the mirror's real path. A first clone runs under the
/// repository's cross-process lock (see below), so two hosts sharing a <see cref="GitWorkspaceOptions.DataRoot"/>
/// normally clone one after the other, the second finding the first's mirror in place. Should both still clone at
/// once — on a host where .NET's file locking is disabled, say — the second to reach the final move finds the
/// destination taken, treats that as the other claimant having won, deletes its own temporary clone, validates the
/// winner's mirror and continues with it. An existing mirror is validated — bare, with
/// <c>remote.origin.fetch</c> set — before it is trusted, and only ever deleted on a <em>positive</em> invalid answer
/// from that validation: a timeout, an ownership refusal, or any other inconclusive result fails the create instead,
/// and never a mirror whose <c>worktrees/</c> directory still has an entry in it, however the validation came out —
/// a mirror with a live worktree is never deleted by this provider, full stop. See <see cref="GitMirrorStore.MirrorValidation"/>.
/// </para>
/// <para>
/// <b>Ownership of a run is claimed atomically, across processes, by publishing a whole record.</b>
/// <see cref="CreateAsync"/> does not check whether the run's record exists and then act on what it saw — a check
/// followed by an action is exactly the race two providers sharing one <see cref="GitWorkspaceOptions.DataRoot"/>
/// (an API host and a CLI host, say) can both pass at once. Instead <see cref="ClaimAsync"/> serialises a
/// <see cref="WorkspaceSidecarState.Provisional"/> record to a uniquely named temp file and publishes it onto the
/// run's sidecar path with <see cref="AtomicPublish.TryPublishNew"/>, which the kernel refuses atomically when the
/// path already exists — a no-replace move on Windows, <c>link(2)</c> on Unix, where .NET's own no-overwrite
/// <see cref="File.Move(string, string, bool)"/> is a check followed by a replacing rename and is not atomic. Exactly
/// one caller's publish can succeed for a given run id, however many processes, repositories or in-process callers
/// race for it. Because the record only ever appears complete, a
/// reader racing the claim sees either no record or the whole one — never a sharing violation and never a
/// half-written file — and a claimant that crashes mid-claim leaves only its own temp file, which
/// <see cref="ListAsync"/> sweeps once it is older than <see cref="PendingTempGracePeriod"/>. The call that loses
/// the publish — including every call for the same run id against a <em>different</em> repository, since the sidecar
/// path is keyed on the run id alone — deletes its own temp file and returns a failure
/// <see cref="Result{T,E}"/>, touching nothing else.
/// </para>
/// <para>
/// <b>The claimant undoes its own work on every path that does not complete.</b> Everything after the claim runs
/// inside one <c>try</c>/<c>finally</c>: a failure <see cref="Result{T,E}"/>, an exception, or a cancellation all
/// run the same undo, which removes the worktree and branch if this call got as far as adding them, and then the
/// sidecar the claim published. Cancellation still propagates as <see cref="OperationCanceledException"/>; the undo
/// itself runs uncancelled, bounded by <see cref="GitWorkspaceOptions.CommandTimeout"/> per git call. The worktree
/// root and the run branch count as this call's own only because the create refuses up front when either already
/// exists, so the undo never deletes something it found.
/// </para>
/// <para>
/// <b>A record is provisional until its create completes, and a run lock says whether its create is alive.</b> The
/// claim is replaced, again by a whole-file move, with a <see cref="WorkspaceSidecarState.Ready"/> record just before
/// observers are told the workspace is ready. <see cref="FindAsync"/> reports only ready workspaces.
/// <see cref="ListAsync"/> reports both, so a sweeper sees a crashed claimant's record. A create holds the run's
/// <see cref="CrossProcessFileLock"/>, <c>locks/runs/&lt;run-id&gt;.lock</c>, from before its claim until its undo, if
/// any, has finished; a create that cannot take it at once fails. <see cref="RemoveAsync"/> tries the same lock
/// without waiting. Held means a create — or another remove — is running for that run, possibly in another process,
/// and the remove is refused however old the record is. Free means no live claimant: the OS released the lock when
/// the claimant finished or died, so the record is removed at once. Age plays no part, so a slow create can never
/// be removed under itself, and a crashed one never waits out a grace period. The lock file is deleted, by its holder,
/// together with the run.
/// </para>
/// <para>
/// <b>A removal is recorded before anyone hears of it.</b> <see cref="RemoveAsync"/>, and the undo of a create that
/// had already published its ready record, first replace the ready record with a
/// <see cref="WorkspaceSidecarState.Removing"/> one, flushed to disk and moved into place like every other record,
/// and only then tell observers the workspace is going. <see cref="FindAsync"/> reports only ready records, so from
/// the moment an observer hears of the removal no caller can find the workspace again, however long the git side
/// takes; an observer that stops something for the run cannot see it started again from this provider's own
/// answer. A git failure leaves the record <see cref="WorkspaceSidecarState.Removing"/>: <see cref="ListAsync"/>
/// still reports it and a later <see cref="RemoveAsync"/> finishes the removal, telling observers again first:
/// <see cref="IRunWorkspaceObserver.OnRemovingAsync"/> is delivered at least once, because the call that marked the
/// record may have been cancelled before every observer heard of it.
/// A record that cannot be marked fails the removal before anything is torn down.
/// </para>
/// <para>
/// <b>Git on a mirror is serialised across processes; that lock has nothing to do with ownership.</b> Every git call
/// this provider makes against a repository's mirror — clone, fetch, worktree add, worktree remove, and branch
/// delete — runs under that repository's lock, which is two locks taken in order: a <see cref="SemaphoreSlim"/> for
/// this process's own callers, and a <see cref="CrossProcessFileLock"/> on <c>locks/mirrors/&lt;repository&gt;.lock</c>
/// for every other process sharing the <see cref="GitWorkspaceOptions.DataRoot"/>. Git's own lock files
/// (<c>config.lock</c>, ref locks, <c>index.lock</c>) prevent corruption, but they make a concurrent second git call
/// fail — "could not lock config file" — rather than wait, so two hosts creating workspaces for one repository at
/// once would fail each other's creates without it. The lock is taken only after ownership is settled by the claim
/// above, and it plays no part in who owns a run. Every lock file lives under <c>&lt;DataRoot&gt;/locks</c>, a
/// namespace no repository or run name can reach, so a repository named like a lock file cannot collide with one.
/// </para>
/// <para>
/// <b><see cref="RemoveAsync"/> converges when git has nothing left to act on.</b> A record whose mirror is gone has
/// nothing to remove on the git side: the run's root directory and record are deleted and the remove succeeds. If
/// something outside this provider deleted <c>&lt;mirror&gt;/worktrees/&lt;id&gt;</c> directly, <c>git worktree
/// remove</c> fails with <c>fatal: '&lt;path&gt;' is not a working tree</c> forever — no amount of retrying
/// changes that outcome. On exactly that error, <see cref="RemoveAsync"/> prunes, force-deletes whatever is left
/// of the run's root directory, deletes the branch, and reports the workspace removed regardless of whether the
/// branch delete itself succeeded — the alternative is a run whose sidecar and root can never be cleaned up by
/// this provider again. The root it deletes is always the one the run id implies, never a path read from the record.
/// </para>
/// </remarks>
/// <param name="options">Where mirrors, worktrees and sidecar records live, and how the git child process is run.</param>
/// <param name="observers">Notified after a create and before a remove; an observer that throws is logged and does not fail the call.</param>
/// <param name="logger">Required: every host has one.</param>
/// <param name="clock">
/// Required: every host has one. Stamps <see cref="RunWorkspace.CreatedAt"/> when the record turns ready (ruling R9).
/// </param>
/// <param name="credentials">
/// Supplies HTTP(S) credentials per remote. <see langword="null"/> means every remote is fetched anonymously — a
/// supported configuration for a public or local remote (ruling R27).
/// </param>
public sealed partial class GitWorktreeWorkspaceProvider(
    GitWorkspaceOptions options,
    IEnumerable<IRunWorkspaceObserver> observers,
    ILogger<GitWorktreeWorkspaceProvider> logger,
    TimeProvider clock,
    IGitCredentialSource? credentials = null) : IRunWorkspaceProvider, IRunBaseFileReader, IRunWorkspaceHandoff
{
    /// <summary>
    /// How old a claim or publish temp file must be before <see cref="ListAsync"/> sweeps it. A write takes
    /// milliseconds, so one this old belongs to a writer that died before its move. Ruling R9's grace, which the
    /// sweeper applies to records with no run row, is the host's own; a provisional record's liveness is its run
    /// lock, not an age.
    /// </summary>
    internal static readonly TimeSpan PendingTempGracePeriod = TimeSpan.FromMinutes(10);

    private const string SidecarSuffix = ".workspace.json";
    private const string PendingSidecarSuffix = ".sidecar.tmp";

    /// <summary>
    /// <see cref="GitWorkspaceOptions.DataRoot"/> in canonical form, taken once. Every path handed to git or to libc
    /// is built from it: the kernel resolves <c>..</c> literally, so a spelling such as <c>root/missing/../data</c>,
    /// which .NET's own file APIs normalise away, fails in <c>link(2)</c> and in git.
    /// </summary>
    private readonly string _dataRoot = Path.GetFullPath(options.DataRoot);

    private readonly GitCli _git = new(options);

    /// <summary>The options this instance was built with, so a host can check that its git consumers share one.</summary>
    internal GitWorkspaceOptions Options => options;

    /// <summary>
    /// The mirror handling: clone, fetch, validation and the per-repository lock. Built from the same options and
    /// credentials, and logging through this provider's own logger.
    /// </summary>
    private readonly GitMirrorStore _mirrors = new(options, new CategoryLogger<GitMirrorStore>(logger), credentials);

    /// <inheritdoc />
    public async ValueTask<Result<RunWorkspace, AgentError>> CreateAsync(RunWorkspaceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Validate(request) is { } invalid)
        {
            return Result<RunWorkspace, AgentError>.Failure(invalid);
        }

        var mirror = MirrorPath(request.Repository);
        var root = WorktreeRoot(request.RunId);
        var (secretConfig, secret) = _mirrors.CredentialConfig(request.Remote);

        // The run lock is this create's liveness signal: held from before the claim until the undo has finished,
        // and released by the OS if this process dies. See the class remarks.
        var runLock = await TryLockRunAsync(request.RunId, ct).ConfigureAwait(false);
        if (runLock.IsFailure)
        {
            return Result<RunWorkspace, AgentError>.Failure(runLock.Error);
        }

        if (runLock.Value is not { } heldRunLock)
        {
            return Result<RunWorkspace, AgentError>.Failure(AgentError.Validation(
                $"A workspace for run '{request.RunId}' is already being created or removed."));
        }

        var runGone = false;
        try
        {
            var claim = await ClaimAsync(request, root, ct).ConfigureAwait(false);
            if (claim.IsFailure)
            {
                return Result<RunWorkspace, AgentError>.Failure(claim.Error);
            }

            // From here this call owns the run. Every way out that is not a completed create — a failure Result, an
            // exception, a cancellation — runs the same undo, so no claimed record outlives its create.
            var progress = new CreateProgress();
            var completed = false;
            try
            {
                var created = await CreateClaimedAsync(request, mirror, root, claim.Value, secretConfig, secret, progress, ct).ConfigureAwait(false);
                completed = created.IsSuccess;
                return created;
            }
            finally
            {
                if (!completed)
                {
                    runGone = await UndoCreateAsync(request, mirror, root, progress).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ReleaseRunLock(heldRunLock, request.RunId, deleteFile: runGone);
        }
    }

    /// <summary>
    /// Claims exclusive, cross-process ownership of <paramref name="request"/>'s run by publishing a whole
    /// provisional record onto its sidecar path — see the class remarks for why an atomic no-replace publish, rather
    /// than a check followed by an action or a write in place, is what makes the claim atomic across every
    /// provider instance sharing this <see cref="GitWorkspaceOptions.DataRoot"/>. A losing caller deletes its own temp
    /// file, returns a failure and touches nothing else. Internal, not private, so its own atomicity can be tested
    /// directly and quickly — hundreds of racing trials with no real git work in any of them — rather than only
    /// indirectly through <see cref="CreateAsync"/>, where a full create's own later git-level collisions (a shared
    /// worktree root refusing a second <c>git worktree add</c>) can coincidentally still converge to one winner even
    /// if this claim were not atomic at all, masking a broken claim rather than exposing it.
    /// </summary>
    internal async Task<Result<RunWorkspace, AgentError>> ClaimAsync(RunWorkspaceRequest request, string root, CancellationToken ct)
    {
        if (EnsureRunsDirectory() is { } unusable)
        {
            return Result<RunWorkspace, AgentError>.Failure(unusable);
        }

        var provisional = new RunWorkspace(request.RunId, request.Repository, request.Remote, request.DefaultBranch, request.Branch, root, SolutionPath: null)
        {
            CreatedAt = clock.GetUtcNow(),
        };

        var sidecar = SidecarPath(request.RunId);
        var temp = PendingSidecarPath(request.RunId);
        try
        {
            if (await WritePendingSidecarAsync(temp, new WorkspaceSidecar(WorkspaceSidecarState.Provisional, provisional), ct).ConfigureAwait(false) is { } writeFailed)
            {
                return Result<RunWorkspace, AgentError>.Failure(writeFailed);
            }

            bool published;
            try
            {
                published = AtomicPublish.TryPublishNew(temp, sidecar);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Result<RunWorkspace, AgentError>.Failure(
                    AgentError.StoreError($"Could not publish the claim for run '{request.RunId}' at '{sidecar}'.", ex.Message));
            }

            // Only an existing record means another claimant owns this run: in this process or another sharing the
            // DataRoot, possibly for a different repository, since the sidecar path is keyed on the run id alone.
            return published
                ? Result<RunWorkspace, AgentError>.Success(provisional)
                : Result<RunWorkspace, AgentError>.Failure(AgentError.Validation($"A workspace for run '{request.RunId}' already exists or is being created."));
        }
        finally
        {
            // A no-op after a successful move; otherwise this call's own temp file, and nobody else's.
            DeleteFileIfPresent(temp, "remove the claim's temp file");
        }
    }

    /// <summary>
    /// The rest of <see cref="CreateAsync"/> once <paramref name="provisional"/>'s claim is secure. Records in
    /// <paramref name="progress"/> how far it got, so <see cref="UndoCreateAsync"/> removes exactly what this call
    /// created and nothing it found.
    /// </summary>
    private async Task<Result<RunWorkspace, AgentError>> CreateClaimedAsync(
        RunWorkspaceRequest request,
        string mirror,
        string root,
        RunWorkspace provisional,
        IReadOnlyList<(string Key, string Value)>? secretConfig,
        string? secret,
        CreateProgress progress,
        CancellationToken ct)
    {
        if (Directory.Exists(root) || File.Exists(root))
        {
            // Nothing but this run's owner may create its root, and this call owns the run but did not create it:
            // it is left over from something else, and an operator decides what it is.
            return Result<RunWorkspace, AgentError>.Failure(AgentError.Validation(
                $"'{root}' already exists but no workspace record owns it; refusing to use or delete it."));
        }

        string baseCommit;
        using (await _mirrors.LockRepositoryAsync(request.Repository, ct).ConfigureAwait(false))
        {
            var prepared = await _mirrors.PrepareMirrorAsync(mirror, request.Remote, secretConfig, secret, ct).ConfigureAwait(false);
            if (prepared.IsFailure)
            {
                return Result<RunWorkspace, AgentError>.Failure(prepared.Error);
            }

            var branchFree = await EnsureBranchIsFreeAsync(mirror, request.Branch, ct).ConfigureAwait(false);
            if (branchFree.IsFailure)
            {
                return Result<RunWorkspace, AgentError>.Failure(branchFree.Error);
            }

            // Set before the call: a cancellation mid-checkout can leave a partial root, registration and branch, and
            // all three are this call's own, since neither the root nor the branch existed a moment ago.
            progress.WorktreeAttempted = true;
            var added = await AddWorktreeAsync(mirror, root, request, ct).ConfigureAwait(false);
            if (added.IsFailure)
            {
                return Result<RunWorkspace, AgentError>.Failure(added.Error);
            }

            baseCommit = added.Value;
        }

        var solution = ResolveSolution(root, request.Solution);
        if (solution.IsFailure)
        {
            return Result<RunWorkspace, AgentError>.Failure(solution.Error);
        }

        // CreatedAt is stamped again here, as the record turns Ready, not left at the claim instant: the orphan grace
        // a sweeper applies to a workspace with no run row must start once the host can start the run, not before a
        // first clone or a queued mirror lock that may itself take longer than the grace (ruling R9). The claim-time
        // stamp still matters for a provisional record, which ListAsync reports: it only decides when a sweeper first
        // tries to remove the record, and the run lock decides whether that removal goes ahead.
        var workspace = provisional with { SolutionPath = solution.Value, CreatedAt = clock.GetUtcNow(), BaseCommit = baseCommit };
        var published = await PublishSidecarAsync(new WorkspaceSidecar(WorkspaceSidecarState.Ready, workspace), ct).ConfigureAwait(false);
        if (published.IsFailure)
        {
            return Result<RunWorkspace, AgentError>.Failure(published.Error);
        }

        progress.Ready = workspace;
        await NotifyObserversAsync(workspace, removing: false, ct).ConfigureAwait(false);
        return Result<RunWorkspace, AgentError>.Success(workspace);
    }

    /// <summary>
    /// Undoes an incomplete create: if observers were told the workspace was ready, marks the record
    /// <see cref="WorkspaceSidecarState.Removing"/> and then tells them it is going; removes the worktree, its
    /// registration and the run branch if this call attempted to add them; and deletes the claimed sidecar last, so a
    /// crash part-way through the undo still leaves a record a sweeper can see. Runs uncancelled — the caller's token
    /// may be the very reason this runs — and does not throw: a git step's failure is logged, and a repository lock
    /// that cannot be taken is logged and leaves the record in place, provisional or removing, for a later
    /// <see cref="RemoveAsync"/> to finish once this create's run lock is released. Returns whether the record is gone.
    /// </summary>
    private async Task<bool> UndoCreateAsync(RunWorkspaceRequest request, string mirror, string root, CreateProgress progress)
    {
        if (progress.Ready is { } ready)
        {
            // Marked first, as RemoveAsync does, so a caller cannot find the workspace while observers tear down. The
            // undo goes on regardless: its last step deletes the record either way.
            var marked = await PublishSidecarAsync(new WorkspaceSidecar(WorkspaceSidecarState.Removing, ready), CancellationToken.None).ConfigureAwait(false);
            if (marked.IsFailure)
            {
                LogCleanupFailed(logger, "mark the ready record as removing before undoing the create", marked.Error.ToString());
            }

            await NotifyObserversAsync(ready, removing: true, CancellationToken.None).ConfigureAwait(false);
        }

        if (progress.WorktreeAttempted)
        {
            GitMirrorStore.MirrorLease lease;
            try
            {
                lease = await _mirrors.LockRepositoryAsync(request.Repository, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogCleanupFailed(logger, "take the repository lock to undo an incomplete create; its record is left for a later remove", ex.Message);
                return false;
            }

            using (lease)
            {
                await RemoveCreatedWorktreeAsync(mirror, root, request.Branch).ConfigureAwait(false);
            }
        }

        var deleted = DeleteSidecar(request.RunId);
        if (deleted.IsFailure)
        {
            LogCleanupFailed(logger, "delete the claimed sidecar", deleted.Error.ToString());
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reports only a <see cref="WorkspaceSidecarState.Ready"/> workspace: a record still being created is not a
    /// workspace yet, and one being removed is not a workspace any more. An unreadable record is logged and reported
    /// as absent.
    /// </remarks>
    public async ValueTask<RunWorkspace?> FindAsync(Guid runId, CancellationToken ct)
    {
        var path = SidecarPath(runId);
        var read = await ReadSidecarAsync(path, ct).ConfigureAwait(false);
        if (read.Sidecar is { State: WorkspaceSidecarState.Ready } sidecar)
        {
            return sidecar.Workspace;
        }

        if (read.Error is { } error)
        {
            LogUnreadableSidecar(logger, path, error);
        }

        return null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reports provisional and removing records too, so a sweeper can see a crashed claimant's record, or a removal
    /// whose git side failed, and finish it with <see cref="RemoveAsync"/>. Also sweeps claim temp files older than
    /// <see cref="PendingTempGracePeriod"/>.
    /// </remarks>
    public async ValueTask<IReadOnlyList<RunWorkspace>> ListAsync(CancellationToken ct)
    {
        var runsDir = RunsDirectory;
        if (!Directory.Exists(runsDir))
        {
            return [];
        }

        SweepStalePendingSidecars(runsDir);

        var workspaces = new List<RunWorkspace>();
        foreach (var file in Directory.EnumerateFiles(runsDir, "*" + SidecarSuffix))
        {
            ct.ThrowIfCancellationRequested();

            // A record the sweeper cannot read is not a reason to abandon the sweep for every other run — skip it
            // and log, rather than letting one bad file abort ListAsync for everything else.
            var read = await ReadSidecarAsync(file, ct).ConfigureAwait(false);
            if (read.Sidecar is { } sidecar)
            {
                workspaces.Add(sidecar.Workspace);
            }
            else if (read.Error is { } error)
            {
                LogUnreadableSidecar(logger, file, error);
            }
        }

        return workspaces;
    }

    /// <inheritdoc />
    public async ValueTask<UnitResult<AgentError>> RemoveAsync(Guid runId, CancellationToken ct)
    {
        // Never waits: a held run lock means a create or another remove is running for this run right now. See the
        // class remarks.
        var runLock = await TryLockRunAsync(runId, ct).ConfigureAwait(false);
        if (runLock.IsFailure)
        {
            return UnitResult<AgentError>.Failure(runLock.Error);
        }

        if (runLock.Value is not { } heldRunLock)
        {
            return UnitResult<AgentError>.Failure(AgentError.Validation(
                $"The workspace for run '{runId}' is still being created or removed by another call; it can be removed once that call finishes."));
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
            ReleaseRunLock(heldRunLock, runId, deleteFile: runGone);
        }
    }

    /// <summary><see cref="RemoveAsync"/> once it holds the run lock, so no create for the run is alive.</summary>
    private async Task<UnitResult<AgentError>> RemoveLockedAsync(Guid runId, CancellationToken ct)
    {
        var read = await ReadSidecarAsync(SidecarPath(runId), ct).ConfigureAwait(false);
        if (read.Error is { } error)
        {
            return UnitResult<AgentError>.Failure(AgentError.StoreError(
                $"The workspace record for run '{runId}' could not be read; leaving it for an operator.", error));
        }

        if (read.Sidecar is not { } sidecar)
        {
            // Removing an already-removed (or never-created) workspace succeeds: there is nothing left to do.
            return UnitResult<AgentError>.Success();
        }

        // A provisional record here belongs to a claimant that is gone: it would still hold the run lock otherwise.
        var workspace = sidecar.Workspace;
        if (!GitMirrorStore.IsValidRepositoryName(workspace.Repository))
        {
            return UnitResult<AgentError>.Failure(AgentError.Validation(
                $"The workspace record for run '{runId}' names repository '{workspace.Repository}', which is not a valid mirror directory name; leaving it for an operator."));
        }

        // Observers were only ever told a ready workspace exists. The mark comes first, so no caller can find the
        // workspace again while observers stop what they started for it, nor after a git failure below. A Removing
        // record is announced again: the call that marked it may have been cancelled before every observer heard,
        // so OnRemovingAsync is delivered at least once and observers must treat a repeat as a no-op.
        if (sidecar.State == WorkspaceSidecarState.Ready)
        {
            var marked = await PublishSidecarAsync(sidecar with { State = WorkspaceSidecarState.Removing }, ct).ConfigureAwait(false);
            if (marked.IsFailure)
            {
                return marked;
            }
        }

        if (sidecar.State != WorkspaceSidecarState.Provisional)
        {
            await NotifyObserversAsync(workspace, removing: true, ct).ConfigureAwait(false);
        }

        var mirror = MirrorPath(workspace.Repository);
        var root = WorktreeRoot(runId);
        UnitResult<AgentError> result;
        using (await _mirrors.LockRepositoryAsync(workspace.Repository, ct).ConfigureAwait(false))
        {
            result = await RemoveFromGitAsync(mirror, root, workspace.Branch, ct).ConfigureAwait(false);
        }

        return result.IsSuccess ? DeleteSidecar(runId) : result;
    }

    /// <summary>
    /// Removes the run's worktree, registration and branch. A missing mirror means nothing is left to remove on the
    /// git side — there is no repository to run git in — so only the root directory is deleted.
    /// </summary>
    private async Task<UnitResult<AgentError>> RemoveFromGitAsync(string mirror, string root, string branch, CancellationToken ct)
    {
        if (!Directory.Exists(mirror))
        {
            return DeleteRootDirectory(root, "remove the worktree directory of a workspace whose mirror is gone");
        }

        var (removed, pruned, branchDeleted) = await RemoveWorktreeAndBranchAsync(mirror, root, branch, ct).ConfigureAwait(false);
        return InterpretRemoval(root, removed, pruned, branchDeleted);
    }

    /// <inheritdoc />
    public async ValueTask<Result<string?, AgentError>> ReadBaseFileAsync(Guid runId, string relativePath, CancellationToken ct)
    {
        var ws = await FindAsync(runId, ct).ConfigureAwait(false);
        if (ws is null)
        {
            return Result<string?, AgentError>.Failure(AgentError.Validation($"Run '{runId}' has no workspace."));
        }

        if (ws.BaseCommit is null)
        {
            return Result<string?, AgentError>.Failure(AgentError.Validation($"The workspace of run '{runId}' has no recorded base commit."));
        }

        var resolved = WorkspacePath.Resolve(ws.Root, relativePath);
        if (resolved.IsFailure)
        {
            return Result<string?, AgentError>.Failure(resolved.Error);
        }

        // WorkspacePath.Resolve confined the path; the object name is its lexically normalised relative form with
        // forward slashes, which is how git addresses a tree entry on every OS. The path is used as given: git's
        // tree is case-sensitive and stays the source of truth, so a wrongly-cased path reads as absent even on a
        // case-insensitive filesystem (ruling R12).
        var rootFull = Path.GetFullPath(ws.Root);
        var relative = Path.GetRelativePath(rootFull, Path.GetFullPath(Path.Combine(rootFull, relativePath))).Replace(Path.DirectorySeparatorChar, '/');
        var spec = $"{ws.BaseCommit}:{relative}";

        // Run against the mirror, where the objects live, so no git process runs inside a tree an agent has written to.
        var mirror = MirrorPath(ws.Repository);

        // A missing object and a non-blob object, such as a directory's tree, both read as absent.
        var kind = await _git.RunAsync(mirror, ["cat-file", "-t", spec], null, null, ct).ConfigureAwait(false);
        if (!kind.Succeeded)
        {
            return kind.TimedOut
                ? Result<string?, AgentError>.Failure(GitFailure("git cat-file timed out.", kind, secret: null))
                : Result<string?, AgentError>.Success(null);
        }

        if (!string.Equals(kind.StdOut.Trim(), "blob", StringComparison.Ordinal))
        {
            return Result<string?, AgentError>.Success(null);
        }

        // cat-file blob, not show: show would apply textconv.
        var shown = await _git.RunAsync(mirror, ["cat-file", "blob", spec], null, null, ct).ConfigureAwait(false);
        return shown.Succeeded
            ? Result<string?, AgentError>.Success(shown.StdOut)
            : Result<string?, AgentError>.Failure(GitFailure("git cat-file of the base file failed.", shown, secret: null));
    }

    /// <inheritdoc />
    public async ValueTask<Result<RunWorkspace, AgentError>> CheckoutForPublishAsync(Guid runId, CancellationToken ct)
    {
        var ws = await FindAsync(runId, ct).ConfigureAwait(false);
        return ws is null
            ? Result<RunWorkspace, AgentError>.Failure(AgentError.Validation($"Run '{runId}' has no workspace."))
            : Result<RunWorkspace, AgentError>.Success(ws);
    }

    // ---------- mirror + worktree ----------

    /// <summary>Fails with <see cref="AgentError.GitBranchAlreadyExists"/> when <paramref name="branch"/> exists in <paramref name="mirror"/>.</summary>
    private async Task<UnitResult<AgentError>> EnsureBranchIsFreeAsync(string mirror, string branch, CancellationToken ct)
    {
        var exists = await BranchExistsAsync(mirror, branch, ct).ConfigureAwait(false);
        if (exists.IsFailure)
        {
            return UnitResult<AgentError>.Failure(exists.Error);
        }

        return exists.Value
            ? UnitResult<AgentError>.Failure(AgentError.GitBranchAlreadyExists(branch))
            : UnitResult<AgentError>.Success();
    }

    /// <summary>
    /// Whether <paramref name="branch"/> already exists in <paramref name="mirror"/>. Checked before
    /// <c>git worktree add -b</c>, so that a branch present afterwards is known to be this create's own and its undo
    /// may delete it; git would refuse the add for an existing branch anyway.
    /// </summary>
    private async Task<Result<bool, AgentError>> BranchExistsAsync(string mirror, string branch, CancellationToken ct)
    {
        var shown = await _git.RunAsync(mirror, ["show-ref", "--verify", "--quiet", "refs/heads/" + branch], null, null, ct).ConfigureAwait(false);
        if (shown.Succeeded)
        {
            return Result<bool, AgentError>.Success(true);
        }

        return !shown.TimedOut && shown.ExitCode == 1
            ? Result<bool, AgentError>.Success(false)
            : Result<bool, AgentError>.Failure(GitFailure("git show-ref for the run branch failed.", shown, secret: null));
    }

    /// <summary>Adds the run's worktree and returns the full sha it was cut from.</summary>
    private async Task<Result<string, AgentError>> AddWorktreeAsync(string mirror, string root, RunWorkspaceRequest request, CancellationToken ct)
    {
        // --no-track: without it, branch.autoSetupMerge's default writes branch.<Branch>.remote and
        // branch.<Branch>.merge into the mirror's shared config, keyed by this run's own branch name — one more
        // such pair for every run that has ever used this mirror, none of them predictable ahead of time. This
        // provider needs no git-level upstream tracking for the branch it cuts, only the starting commit, so
        // --no-track keeps the mirror's config within MirrorConfigSurface's fixed allow-list (fix round 2 ruling).
        var added = await _git.RunAsync(
            mirror,
            ["worktree", "add", "--no-track", "-b", request.Branch, "--", root, request.StartPoint ?? $"origin/{request.DefaultBranch}"],
            ["core.symlinks=false"],
            null,
            ct).ConfigureAwait(false);

        // A failed add is undone by UndoCreateAsync, together with every other way a create can end incomplete.
        if (!added.Succeeded)
        {
            return Result<string, AgentError>.Failure(GitFailure("git worktree add failed.", added, secret: null));
        }

        var head = await _git.RunAsync(root, ["rev-parse", "HEAD"], null, null, ct).ConfigureAwait(false);
        return head.Succeeded
            ? Result<string, AgentError>.Success(head.StdOut.Trim())
            : Result<string, AgentError>.Failure(GitFailure("git rev-parse HEAD failed.", head, secret: null));
    }

    /// <summary>
    /// The git half of <see cref="UndoCreateAsync"/>: removes the worktree this create added, or whatever part of it
    /// an interrupted add left — its directory and, through <c>worktree prune</c>, its registration — and the run
    /// branch. Uncancelled; each failure is logged. A branch the add never got to create is not an error.
    /// </summary>
    private async Task RemoveCreatedWorktreeAsync(string mirror, string root, string branch)
    {
        var removed = await _git.RunAsync(mirror, ["worktree", "remove", "--force", "--", root], null, null, CancellationToken.None).ConfigureAwait(false);
        if (!removed.Succeeded)
        {
            TryDeleteDirectory(root, "remove the worktree directory of an incomplete create");
            var pruned = await _git.RunAsync(mirror, ["worktree", "prune"], null, null, CancellationToken.None).ConfigureAwait(false);
            if (!pruned.Succeeded)
            {
                LogCleanupFailed(logger, "prune the stale worktree registration", ExtractErrorDetail(pruned.StdErr));
            }
        }

        var branchDeleted = await _git.RunAsync(mirror, ["branch", "-D", "--", branch], null, null, CancellationToken.None).ConfigureAwait(false);
        if (!branchDeleted.Succeeded && !branchDeleted.StdErr.Contains("not found", StringComparison.Ordinal))
        {
            LogCleanupFailed(logger, "delete the run branch", ExtractErrorDetail(branchDeleted.StdErr));
        }
    }

    /// <summary>
    /// Removes the worktree, and — whether or not that succeeds — always also attempts <c>worktree prune</c> (only
    /// when the removal failed, to drop a stale registration) and <c>branch -D</c>, so a repository already missing
    /// its worktree directory on disk, or otherwise in a bad state, still gets as much cleanup as git can do.
    /// </summary>
    private async Task<(GitCliResult Removed, GitCliResult? Pruned, GitCliResult BranchDeleted)> RemoveWorktreeAndBranchAsync(string mirror, string root, string branch, CancellationToken ct)
    {
        var removed = await _git.RunAsync(mirror, ["worktree", "remove", "--force", "--", root], null, null, ct).ConfigureAwait(false);

        GitCliResult? pruned = null;
        if (!removed.Succeeded)
        {
            pruned = await _git.RunAsync(mirror, ["worktree", "prune"], null, null, ct).ConfigureAwait(false);
        }

        var branchDeleted = await _git.RunAsync(mirror, ["branch", "-D", "--", branch], null, null, ct).ConfigureAwait(false);
        return (removed, pruned, branchDeleted);
    }

    /// <summary>
    /// Turns the three <see cref="RemoveWorktreeAndBranchAsync"/> results into the overall outcome of a remove.
    /// A <c>worktree remove</c> failure whose message is <see cref="IsNotAWorkingTree"/> converges instead of
    /// failing forever: the worktree's own administrative directory is already gone, so no retry of the same
    /// command will ever succeed — <paramref name="pruned"/>'s and <paramref name="branchDeleted"/>'s own failures
    /// are logged rather than surfaced, <paramref name="root"/> is force-deleted directly, and the workspace is
    /// reported removed once that directory is gone.
    /// </summary>
    private UnitResult<AgentError> InterpretRemoval(string root, GitCliResult removed, GitCliResult? pruned, GitCliResult branchDeleted)
    {
        if (!removed.Succeeded && IsNotAWorkingTree(removed.StdErr))
        {
            if (pruned is { Succeeded: false } prunedAfterMissingAdmin)
            {
                LogCleanupFailed(logger, "prune the stale worktree registration", ExtractErrorDetail(prunedAfterMissingAdmin.StdErr));
            }

            if (!branchDeleted.Succeeded)
            {
                LogCleanupFailed(logger, "delete the run branch", ExtractErrorDetail(branchDeleted.StdErr));
            }

            return DeleteRootDirectory(root, "remove the worktree directory directly, since git no longer considers it a working tree");
        }

        if (!removed.Succeeded)
        {
            if (pruned is { Succeeded: false } p)
            {
                LogCleanupFailed(logger, "prune the stale worktree registration", ExtractErrorDetail(p.StdErr));
            }

            if (!branchDeleted.Succeeded)
            {
                LogCleanupFailed(logger, "delete the run branch", ExtractErrorDetail(branchDeleted.StdErr));
            }

            return UnitResult<AgentError>.Failure(GitFailure("git worktree remove failed.", removed, secret: null));
        }

        return branchDeleted.Succeeded
            ? UnitResult<AgentError>.Success()
            : UnitResult<AgentError>.Failure(GitFailure("git branch -D failed.", branchDeleted, secret: null));
    }

    private static Result<string?, AgentError> ResolveSolution(string root, string? solution)
    {
        if (solution is null)
        {
            return Result<string?, AgentError>.Success(null);
        }

        var resolved = WorkspacePath.Resolve(root, solution);
        return resolved.IsSuccess
            ? Result<string?, AgentError>.Success(resolved.Value)
            : Result<string?, AgentError>.Failure(resolved.Error);
    }

    // ---------- observers ----------

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
            // Only the caller's own cancellation stops the loop. An observer's own OperationCanceledException, such as
            // an HttpClient timeout, is a failure of that observer like any other: logged, and every later observer
            // is still told.
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                LogObserverFailed(logger, workspace.RunId, removing ? nameof(IRunWorkspaceObserver.OnRemovingAsync) : nameof(IRunWorkspaceObserver.OnReadyAsync), ex.Message);
            }
        }
    }
    // ---------- credentials ----------

    private static AgentError GitFailure(string message, GitCliResult result, string? secret) =>
        GitMirrorStore.GitFailure(message, result, secret);

    /// <inheritdoc cref="GitMirrorStore.ExtractErrorDetail"/>
    internal static string ExtractErrorDetail(string stdErr) => GitMirrorStore.ExtractErrorDetail(stdErr);

    /// <summary>
    /// <see langword="true"/> when <paramref name="stdErr"/> is git's own <c>fatal: '&lt;path&gt;' is not a
    /// working tree</c> — the error <c>git worktree remove</c> gives when the worktree's administrative directory
    /// under the mirror is already gone, which no retry of the same command will ever turn into a success.
    /// </summary>
    private static bool IsNotAWorkingTree(string stdErr) =>
        stdErr.Contains("is not a working tree", StringComparison.Ordinal);
    // ---------- validation ----------

    private static AgentError? Validate(RunWorkspaceRequest request) => RunWorkspaceRequestValidator.Validate(request);

    // ---------- paths ----------

    private string RunsDirectory => Path.Combine(_dataRoot, "runs");

    /// <summary>Every lock file, and nothing else, lives here: no repository or run name can reach it.</summary>
    private string LocksDirectory => Path.Combine(_dataRoot, "locks");

    private string RunLockPath(Guid runId) => Path.Combine(LocksDirectory, "runs", runId.ToString() + ".lock");

    /// <summary>
    /// Takes the run's lock without waiting: the held lock, <see langword="null"/> when another call holds it, or a
    /// failure when the lock file cannot be opened at all.
    /// </summary>
    private async Task<Result<FileStream?, AgentError>> TryLockRunAsync(Guid runId, CancellationToken ct)
    {
        try
        {
            return Result<FileStream?, AgentError>.Success(await CrossProcessFileLock.TryAcquireAsync(RunLockPath(runId), ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<FileStream?, AgentError>.Failure(AgentError.StoreError($"Could not open the lock for run '{runId}'.", ex.Message));
        }
    }

    /// <summary>
    /// Releases the run's lock, deleting its file first, while still held, when the run is gone. A failed delete is
    /// logged: a leftover lock file is empty and harmless, and the next holder of that run reuses it.
    /// </summary>
    private void ReleaseRunLock(FileStream held, Guid runId, bool deleteFile)
    {
        if (!deleteFile)
        {
            held.Dispose();
            return;
        }

        try
        {
            CrossProcessFileLock.DeleteHeld(held, RunLockPath(runId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCleanupFailed(logger, $"delete the lock file of run '{runId}'", ex.Message);
        }
    }

    private string MirrorPath(string repository) => _mirrors.MirrorPath(repository);

    private string WorktreeRoot(Guid runId) => Path.Combine(RunsDirectory, runId.ToString());

    private string SidecarPath(Guid runId) => Path.Combine(RunsDirectory, runId.ToString() + SidecarSuffix);

    /// <summary>
    /// A temp path unique to one write, so two writers never share one, and named so it never matches
    /// <see cref="SidecarSuffix"/>: nothing reads a temp file as a record.
    /// </summary>
    private string PendingSidecarPath(Guid runId) =>
        Path.Combine(RunsDirectory, $".{runId:N}.{Guid.NewGuid():N}{PendingSidecarSuffix}");

    // ---------- sidecar store ----------

    /// <summary>Creates the runs directory, or says accurately why it cannot be used.</summary>
    private AgentError? EnsureRunsDirectory()
    {
        var runsDir = RunsDirectory;
        try
        {
            Directory.CreateDirectory(runsDir);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return File.Exists(runsDir)
                ? AgentError.StoreError($"The runs path '{runsDir}' exists but is not a directory.")
                : AgentError.StoreError($"Could not create the runs directory '{runsDir}'.", ex.Message);
        }
    }

    /// <summary>
    /// Writes <paramref name="sidecar"/> completely to <paramref name="temp"/>, a path no one else uses, and flushes
    /// it to disk, so the move that publishes it can only ever publish a complete record.
    /// </summary>
    private static async Task<AgentError?> WritePendingSidecarAsync(string temp, WorkspaceSidecar sidecar, CancellationToken ct)
    {
        try
        {
            var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, sidecar, GitWorkspaceJsonContext.Default.WorkspaceSidecar, ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AgentError.StoreError($"Could not write the workspace record '{temp}'.", ex.Message);
        }
    }

    /// <summary>
    /// Replaces this run's record with <paramref name="sidecar"/> by moving a complete temp file over it, so a reader
    /// sees the old record or the new one, never part of either.
    /// </summary>
    private async Task<UnitResult<AgentError>> PublishSidecarAsync(WorkspaceSidecar sidecar, CancellationToken ct)
    {
        var temp = PendingSidecarPath(sidecar.Workspace.RunId);
        try
        {
            if (await WritePendingSidecarAsync(temp, sidecar, ct).ConfigureAwait(false) is { } writeFailed)
            {
                return UnitResult<AgentError>.Failure(writeFailed);
            }

            File.Move(temp, SidecarPath(sidecar.Workspace.RunId), overwrite: true);
            return UnitResult<AgentError>.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UnitResult<AgentError>.Failure(AgentError.StoreError(
                $"Could not publish the workspace record for run '{sidecar.Workspace.RunId}'.", ex.Message));
        }
        finally
        {
            DeleteFileIfPresent(temp, "remove the workspace record's temp file");
        }
    }

    /// <summary>The outcome of reading one record: absent, read, or unreadable with the reason.</summary>
    private readonly record struct SidecarRead(WorkspaceSidecar? Sidecar, string? Error);

    /// <summary>
    /// Reads a record without ever throwing for its absence or its content. The file is opened sharing read, write
    /// and delete, so reading never blocks the whole-file move that publishes a newer record or the delete that
    /// removes it; a reader holding the old file open simply finishes reading the old, complete, record.
    /// </summary>
    private static async Task<SidecarRead> ReadSidecarAsync(string path, CancellationToken ct)
    {
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            await using (stream.ConfigureAwait(false))
            {
                var sidecar = await JsonSerializer.DeserializeAsync(stream, GitWorkspaceJsonContext.Default.WorkspaceSidecar, ct).ConfigureAwait(false);
                return sidecar?.Workspace is null
                    ? new SidecarRead(null, "The record holds no workspace.")
                    : new SidecarRead(sidecar, null);
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new SidecarRead(null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new SidecarRead(null, ex.Message);
        }
    }

    private UnitResult<AgentError> DeleteSidecar(Guid runId)
    {
        try
        {
            File.Delete(SidecarPath(runId));
            return UnitResult<AgentError>.Success();
        }
        catch (DirectoryNotFoundException)
        {
            return UnitResult<AgentError>.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UnitResult<AgentError>.Failure(AgentError.StoreError($"Could not delete the workspace record for run '{runId}'.", ex.Message));
        }
    }

    /// <summary>
    /// Deletes claim and publish temp files older than <see cref="PendingTempGracePeriod"/>: a write takes
    /// milliseconds, so one that old belongs to a writer that crashed before its move. A younger one may still be
    /// about to be moved into place, and is left alone.
    /// </summary>
    private void SweepStalePendingSidecars(string runsDir)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var file in Directory.EnumerateFiles(runsDir, "*" + PendingSidecarSuffix))
        {
            if (now - File.GetLastWriteTimeUtc(file) >= PendingTempGracePeriod)
            {
                DeleteFileIfPresent(file, "sweep a stale workspace record temp file");
            }
        }
    }

    private void DeleteFileIfPresent(string path, string what)
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone with its directory.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCleanupFailed(logger, what, ex.Message);
        }
    }

    /// <summary>Deletes <paramref name="path"/> recursively if present, logging a failure; reports whether it is gone.</summary>
    private bool TryDeleteDirectory(string path, string what)
    {
        if (!Directory.Exists(path))
        {
            return true;
        }

        try
        {
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCleanupFailed(logger, what, ex.Message);
            return false;
        }
    }

    /// <summary>A remove reports success only once the run's root directory is actually gone.</summary>
    private UnitResult<AgentError> DeleteRootDirectory(string root, string what) =>
        TryDeleteDirectory(root, what)
            ? UnitResult<AgentError>.Success()
            : UnitResult<AgentError>.Failure(AgentError.StoreError($"Could not delete the worktree directory '{root}'."));

    /// <summary>How far one create got after its claim, so its undo removes exactly what it created.</summary>
    private sealed class CreateProgress
    {
        /// <summary>
        /// <c>git worktree add</c> was started. The root and branch did not exist beforehand, so whatever of them
        /// exists now is this create's own.
        /// </summary>
        public bool WorktreeAttempted { get; set; }

        /// <summary>The ready record was published and observers are being told; they must hear of the removal too.</summary>
        public RunWorkspace? Ready { get; set; }
    }

    [LoggerMessage(EventId = 1000, Level = LogLevel.Warning, Message = "Workspace observer for run {RunId} failed during {Phase}: {Error}")]
    private static partial void LogObserverFailed(ILogger logger, Guid runId, string phase, string error);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "Could not {What}: {Error}")]
    private static partial void LogCleanupFailed(ILogger logger, string what, string error);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning, Message = "Sidecar '{Path}' could not be read and was skipped: {Error}")]
    private static partial void LogUnreadableSidecar(ILogger logger, string path, string error);
}
