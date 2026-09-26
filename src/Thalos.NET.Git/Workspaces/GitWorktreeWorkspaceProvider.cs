using System.Collections.Concurrent;
using System.Text;
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
/// a mirror with a live worktree is never deleted by this provider, full stop. See <see cref="MirrorValidation"/>.
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
/// <param name="clock">Required: every host has one. Stamps <see cref="RunWorkspace.CreatedAt"/> (ruling R9).</param>
/// <param name="credentials">
/// Supplies HTTP(S) credentials per remote. <see langword="null"/> means every remote is fetched anonymously — a
/// supported configuration for a public or local remote (ruling R27).
/// </param>
public sealed partial class GitWorktreeWorkspaceProvider(
    GitWorkspaceOptions options,
    IEnumerable<IRunWorkspaceObserver> observers,
    ILogger<GitWorktreeWorkspaceProvider> logger,
    TimeProvider clock,
    IGitCredentialSource? credentials = null) : IRunWorkspaceProvider
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
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _repositoryLocks = new(StringComparer.Ordinal);

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
        var (secretConfig, secret) = CredentialConfig(request.Remote);

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

        using (await LockRepositoryAsync(request.Repository, ct).ConfigureAwait(false))
        {
            var prepared = await PrepareMirrorAsync(mirror, request.Remote, secretConfig, secret, ct).ConfigureAwait(false);
            if (prepared.IsFailure)
            {
                return Result<RunWorkspace, AgentError>.Failure(prepared.Error);
            }

            var branchExists = await BranchExistsAsync(mirror, request.Branch, ct).ConfigureAwait(false);
            if (branchExists.IsFailure)
            {
                return Result<RunWorkspace, AgentError>.Failure(branchExists.Error);
            }

            if (branchExists.Value)
            {
                return Result<RunWorkspace, AgentError>.Failure(AgentError.GitBranchAlreadyExists(request.Branch));
            }

            // Set before the call: a cancellation mid-checkout can leave a partial root, registration and branch, and
            // all three are this call's own, since neither the root nor the branch existed a moment ago.
            progress.WorktreeAttempted = true;
            var added = await AddWorktreeAsync(mirror, root, request, ct).ConfigureAwait(false);
            if (added.IsFailure)
            {
                return Result<RunWorkspace, AgentError>.Failure(added.Error);
            }
        }

        var solution = ResolveSolution(root, request.Solution);
        if (solution.IsFailure)
        {
            return Result<RunWorkspace, AgentError>.Failure(solution.Error);
        }

        var workspace = provisional with { SolutionPath = solution.Value };
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
            MirrorLease lease;
            try
            {
                lease = await LockRepositoryAsync(request.Repository, CancellationToken.None).ConfigureAwait(false);
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
        if (!IsValidRepositoryName(workspace.Repository))
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
        using (await LockRepositoryAsync(workspace.Repository, ct).ConfigureAwait(false))
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

    // ---------- mirror + worktree ----------

    private async Task<UnitResult<AgentError>> PrepareMirrorAsync(string mirror, string remote, IReadOnlyList<(string Key, string Value)>? secretConfig, string? secret, CancellationToken ct)
    {
        if (Directory.Exists(mirror))
        {
            var (state, detail) = await ValidateMirrorAsync(mirror, ct).ConfigureAwait(false);
            switch (state)
            {
                case MirrorValidation.Valid:
                    break;

                case MirrorValidation.Invalid when HasLiveWorktrees(mirror):
                    return UnitResult<AgentError>.Failure(AgentError.Validation(
                        $"The mirror at '{mirror}' looks invalid but still has live worktrees; refusing to delete it. An operator must resolve this."));

                case MirrorValidation.Invalid:
                    TryDeleteDirectory(mirror, "remove the invalid mirror before re-cloning");
                    break;

                // A positive answer — the mirror's own config carries a key outside MirrorConfigSurface's
                // allow-list — but never grounds for deletion, unlike Invalid: the mirror is left exactly as
                // found, for an operator to inspect, not silently deleted and recloned (fix round 2 ruling).
                case MirrorValidation.ConfigNotAllowed:
                    return UnitResult<AgentError>.Failure(AgentError.Validation(
                        $"The mirror at '{mirror}' has git config outside the allowed surface; refusing it. An operator must resolve this. Detail: {detail}"));

                // Indeterminate: a timeout, a validation command that failed for a reason other than a definitive
                // "not bare" or "no fetch refspec" answer, or dubious ownership. None of these is a positive
                // signal the mirror is invalid, so it is never deleted on this path — only a positive answer is
                // grounds for deletion (see the class remarks); the create simply fails and an operator decides.
                // detail carries git's own extracted error line (e.g. "dubious ownership"), so an operator sees
                // why validation could not reach a definitive answer, not just that it didn't.
                default:
                    return UnitResult<AgentError>.Failure(AgentError.GitOperationFailed(
                        $"Could not validate the mirror at '{mirror}'; leaving it untouched.", detail));
            }
        }

        if (!Directory.Exists(mirror))
        {
            var cloned = await CloneMirrorAsync(mirror, remote, secretConfig, secret, ct).ConfigureAwait(false);
            if (cloned.IsFailure)
            {
                return cloned;
            }
        }

        return await RetargetFetchAndValidateAsync(mirror, remote, secretConfig, secret, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-targets an existing or freshly cloned mirror at <paramref name="remote"/>, fetches, and only then runs
    /// the full <see cref="MirrorConfigSurface"/> check — <c>remote.origin.url</c>'s own value included. Running
    /// that full check here, after this call's own write, rather than before it, is deliberate (fix round 3
    /// ruling; see <see cref="MirrorConfigSurface"/>'s own remarks for why the timing matters): the earlier,
    /// pre-fetch <see cref="ValidateMirrorAsync"/> check passes <c>remote: null</c>, since <c>remote.origin.url</c>
    /// legitimately still holds an older run's value at that point, for a mirror this create is reusing and about
    /// to re-target. By the time this method's own write has run, it holds this create's own value, so checking it
    /// strictly here catches a genuine tamper without ever refusing a legitimate remote change.
    /// </summary>
    private async Task<UnitResult<AgentError>> RetargetFetchAndValidateAsync(
        string mirror, string remote, IReadOnlyList<(string Key, string Value)>? secretConfig, string? secret, CancellationToken ct)
    {
        // Before every fetch, not only on first clone: a mirror kept for a repository name can otherwise go on
        // fetching (and sending credentials to) a remote the current request no longer names.
        var setUrl = await _git.RunAsync(mirror, ["config", "remote.origin.url", remote], null, null, ct).ConfigureAwait(false);
        if (!setUrl.Succeeded)
        {
            return UnitResult<AgentError>.Failure(GitFailure("git config remote.origin.url failed.", setUrl, secret: null));
        }

        var fetched = await _git.RunAsync(mirror, ["fetch", "-q", "origin", "--prune"], null, secretConfig, ct).ConfigureAwait(false);
        if (!fetched.Succeeded)
        {
            return UnitResult<AgentError>.Failure(GitFailure("git fetch failed.", fetched, secret));
        }

        var violation = await MirrorConfigSurface.FindViolationAsync(_git, mirror, remote, ct).ConfigureAwait(false);
        if (violation is not null)
        {
            return UnitResult<AgentError>.Failure(AgentError.Validation(
                $"The mirror at '{mirror}' has git config outside the allowed surface after this create's own update; refusing it. An operator must resolve this. Detail: {violation}"));
        }

        return UnitResult<AgentError>.Success();
    }

    private async Task<UnitResult<AgentError>> CloneMirrorAsync(string mirror, string remote, IReadOnlyList<(string Key, string Value)>? secretConfig, string? secret, CancellationToken ct)
    {
        var repository = Path.GetFileName(mirror);
        var mirrorsDir = Path.Combine(_dataRoot, "mirrors");
        Directory.CreateDirectory(mirrorsDir);

        // This call holds the repository's mirror lock (see CreateClaimedAsync), so no other clone of the same
        // repository can be in progress right now — any .tmp-* directory already here for this repository was left
        // by a clone that was killed mid-way, never cleaned up on its own path, and is safe to delete. A directory
        // left by a concurrent clone of a *different* repository, under its own lock, is untouched: the name
        // carries the repository so the sweep never reaches across repositories.
        SweepStaleCloneTempDirectories(mirrorsDir, repository);
        var temp = Path.Combine(mirrorsDir, TempCloneDirectoryName(repository));
        try
        {
            var cloned = await _git.RunAsync(mirrorsDir, ["clone", "--bare", "-q", "--", remote, temp], null, secretConfig, ct).ConfigureAwait(false);
            if (!cloned.Succeeded)
            {
                return UnitResult<AgentError>.Failure(GitFailure("git clone --bare failed.", cloned, secret));
            }

            var fetchSpec = await _git.RunAsync(temp, ["config", "remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*"], null, null, ct).ConfigureAwait(false);
            if (!fetchSpec.Succeeded)
            {
                return UnitResult<AgentError>.Failure(GitFailure("git config remote.origin.fetch failed.", fetchSpec, secret: null));
            }

            // Persisted on the mirror in addition to being passed again on every worktree checkout below — see the
            // class remarks for why symlinks must never reach a workspace this provider creates.
            var noSymlinks = await _git.RunAsync(temp, ["config", "core.symlinks", "false"], null, null, ct).ConfigureAwait(false);
            if (!noSymlinks.Succeeded)
            {
                return UnitResult<AgentError>.Failure(GitFailure("git config core.symlinks failed.", noSymlinks, secret: null));
            }

            try
            {
                Directory.Move(temp, mirror);
            }
            catch (IOException) when (Directory.Exists(mirror))
            {
                // Another claimant, most likely another host process sharing this DataRoot, finished its own first
                // clone of this repository first. Its mirror is complete, because it too moved it into place only
                // after clone and config succeeded; validate it and continue with it rather than failing a create
                // that merely lost a race. This call's own clone is deleted by the finally below.
                return await AdoptRacedMirrorAsync(mirror, ct).ConfigureAwait(false);
            }

            return UnitResult<AgentError>.Success();
        }
        finally
        {
            // Runs whether the clone succeeded (the temp directory has already been moved away and this is a
            // no-op) or failed at any step (nothing is left half-cloned at either the temp or the real path).
            TryDeleteDirectory(temp, "remove the temporary clone directory");
        }
    }

    /// <summary>
    /// Continues with a mirror another claimant moved into place while this call was cloning, after checking it is a
    /// valid one. A mirror that does not validate is left alone — this call did not create it — and fails the create.
    /// </summary>
    private async Task<UnitResult<AgentError>> AdoptRacedMirrorAsync(string mirror, CancellationToken ct)
    {
        LogFirstCloneRaceLost(logger, mirror);
        var (state, detail) = await ValidateMirrorAsync(mirror, ct).ConfigureAwait(false);
        return state == MirrorValidation.Valid
            ? UnitResult<AgentError>.Success()
            : UnitResult<AgentError>.Failure(AgentError.GitOperationFailed(
                $"Another process cloned the mirror at '{mirror}' first, and it did not validate; leaving it untouched.", detail));
    }

    /// <summary>
    /// Whether an existing mirror is usable as-is (<see cref="Valid"/>), positively confirmed unusable and eligible
    /// for deletion (<see cref="Invalid"/>), positively confirmed to carry git config outside
    /// <see cref="MirrorConfigSurface"/>'s allow-list (<see cref="ConfigNotAllowed"/>), or neither confirmed
    /// (<see cref="Indeterminate"/>) — a validation command that failed to give either a clear "yes, bare, with a
    /// fetch refspec" or a clear "no" answer, such as a timeout or a "dubious ownership" refusal. Only
    /// <see cref="Invalid"/> ever leads to deleting the mirror; <see cref="ConfigNotAllowed"/> and
    /// <see cref="Indeterminate"/> both fail the create and leave the mirror exactly as found.
    /// </summary>
    private enum MirrorValidation
    {
        Valid,
        Invalid,
        Indeterminate,
        ConfigNotAllowed,
    }

    /// <summary>
    /// Validates an existing mirror without ever guessing: <see cref="MirrorValidation.Invalid"/> only for a
    /// positive invalid answer — <c>rev-parse --is-bare-repository</c> succeeding and printing exactly
    /// <c>"false"</c>, or <c>config --get remote.origin.fetch</c> exiting exactly 1 (git's own convention for "key
    /// not found") after a positive <c>"true"</c>. Every other outcome — a timeout, a nonzero exit that is not
    /// exactly that convention, an unrecognised <c>rev-parse</c> answer — is <see cref="MirrorValidation.Indeterminate"/>,
    /// never treated as invalid, and carries git's own extracted error detail (e.g. a "dubious ownership" refusal)
    /// so a failed create says why, not just that validation could not reach a definitive answer. A mirror that is
    /// otherwise bare with a fetch refspec is still checked against <see cref="MirrorConfigSurface"/>'s allow-list —
    /// keys, and, for the keys the provider itself writes other than <c>remote.origin.url</c>, their exact values
    /// too (fix round 2 and 3 rulings). <c>remote.origin.url</c>'s own value is deliberately not checked here: this
    /// call runs before <c>PrepareMirrorAsync</c> overwrites it for a mirror being reused with a possibly different
    /// remote, so it may still legitimately hold an earlier run's value; <c>PrepareMirrorAsync</c> checks it
    /// strictly, separately, right after writing it — see <see cref="MirrorConfigSurface"/>'s own remarks.
    /// </summary>
    /// <param name="mirror">The mirror to validate.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<(MirrorValidation State, string? Detail)> ValidateMirrorAsync(string mirror, CancellationToken ct)
    {
        var isBare = await _git.RunAsync(mirror, ["rev-parse", "--is-bare-repository"], null, null, ct).ConfigureAwait(false);
        if (!isBare.Succeeded)
        {
            return (MirrorValidation.Indeterminate, IndeterminateDetail(isBare, "rev-parse --is-bare-repository"));
        }

        var isBareAnswer = isBare.StdOut.Trim();
        if (string.Equals(isBareAnswer, "false", StringComparison.Ordinal))
        {
            return (MirrorValidation.Invalid, null);
        }

        if (!string.Equals(isBareAnswer, "true", StringComparison.Ordinal))
        {
            return (MirrorValidation.Indeterminate, $"rev-parse --is-bare-repository printed an unrecognised answer: '{isBareAnswer}'.");
        }

        var fetchSpec = await _git.RunAsync(mirror, ["config", "--get", "remote.origin.fetch"], null, null, ct).ConfigureAwait(false);
        if (!fetchSpec.Succeeded || string.IsNullOrWhiteSpace(fetchSpec.StdOut))
        {
            return !fetchSpec.TimedOut && fetchSpec.ExitCode == 1
                ? (MirrorValidation.Invalid, null)
                : (MirrorValidation.Indeterminate, IndeterminateDetail(fetchSpec, "config --get remote.origin.fetch"));
        }

        var violation = await MirrorConfigSurface.FindViolationAsync(_git, mirror, remote: null, ct).ConfigureAwait(false);
        return violation is null
            ? (MirrorValidation.Valid, null)
            : (MirrorValidation.ConfigNotAllowed, violation);
    }

    private static string IndeterminateDetail(GitCliResult result, string command) =>
        result.TimedOut ? $"{command} timed out." : ExtractErrorDetail(result.StdErr);

    /// <summary>
    /// Whether <paramref name="mirror"/>'s <c>worktrees/</c> administrative directory has any entries — a positive
    /// answer means at least one worktree, live or merely registered, still points at this mirror, so it must never
    /// be deleted regardless of what <see cref="ValidateMirrorAsync"/> says.
    /// </summary>
    private static bool HasLiveWorktrees(string mirror)
    {
        var worktreesDir = Path.Combine(mirror, "worktrees");
        return Directory.Exists(worktreesDir) && Directory.EnumerateFileSystemEntries(worktreesDir).Any();
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

    private async Task<UnitResult<AgentError>> AddWorktreeAsync(string mirror, string root, RunWorkspaceRequest request, CancellationToken ct)
    {
        // --no-track: without it, branch.autoSetupMerge's default writes branch.<Branch>.remote and
        // branch.<Branch>.merge into the mirror's shared config, keyed by this run's own branch name — one more
        // such pair for every run that has ever used this mirror, none of them predictable ahead of time. This
        // provider needs no git-level upstream tracking for the branch it cuts, only the starting commit, so
        // --no-track keeps the mirror's config within MirrorConfigSurface's fixed allow-list (fix round 2 ruling).
        var added = await _git.RunAsync(
            mirror,
            ["worktree", "add", "--no-track", "-b", request.Branch, "--", root, $"origin/{request.DefaultBranch}"],
            ["core.symlinks=false"],
            null,
            ct).ConfigureAwait(false);

        // A failed add is undone by UndoCreateAsync, together with every other way a create can end incomplete.
        return added.Succeeded
            ? UnitResult<AgentError>.Success()
            : UnitResult<AgentError>.Failure(GitFailure("git worktree add failed.", added, secret: null));
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

    private (IReadOnlyList<(string Key, string Value)>? SecretConfig, string? Secret) CredentialConfig(string remoteUrl)
    {
        if (credentials?.GetCredentials(remoteUrl) is not { } creds)
        {
            return (null, null);
        }

        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{creds.Username}:{creds.Password}"));
        return ([("http.extraHeader", $"AUTHORIZATION: basic {token}")], token);
    }

    private static AgentError GitFailure(string message, GitCliResult result, string? secret)
    {
        if (result.TimedOut)
        {
            return AgentError.GitOperationFailed($"{message} The git command timed out.");
        }

        return AgentError.GitOperationFailed(message, Scrub(ExtractErrorDetail(result.StdErr), secret));
    }

    private static string Scrub(string text, string? secret) =>
        secret is null ? text : text.Replace(secret, "***", StringComparison.Ordinal);

    /// <summary>
    /// <see langword="true"/> when <paramref name="stdErr"/> is git's own <c>fatal: '&lt;path&gt;' is not a
    /// working tree</c> — the error <c>git worktree remove</c> gives when the worktree's administrative directory
    /// under the mirror is already gone, which no retry of the same command will ever turn into a success.
    /// </summary>
    private static bool IsNotAWorkingTree(string stdErr) =>
        stdErr.Contains("is not a working tree", StringComparison.Ordinal);

    /// <summary>
    /// The first line starting with <c>fatal:</c> or <c>error:</c> — git's own convention for the line that names
    /// what went wrong, buried among progress and hint lines <c>-q</c> does not suppress — or, when no line
    /// matches, the last non-empty line, which is usually the most specific one available. Internal (not private)
    /// so it can be unit-tested directly.
    /// </summary>
    internal static string ExtractErrorDetail(string stdErr)
    {
        string? lastNonEmpty = null;
        using var reader = new StringReader(stdErr);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            lastNonEmpty = trimmed;
            if (trimmed.StartsWith("fatal:", StringComparison.Ordinal) || trimmed.StartsWith("error:", StringComparison.Ordinal))
            {
                return trimmed;
            }
        }

        return lastNonEmpty ?? string.Empty;
    }

    // ---------- validation ----------

    private static AgentError? Validate(RunWorkspaceRequest request)
    {
        if (!IsValidRepositoryName(request.Repository))
        {
            return AgentError.Validation($"Repository '{request.Repository}' is not a valid mirror directory name.");
        }

        if (string.IsNullOrWhiteSpace(request.Remote))
        {
            return AgentError.Validation("Remote must not be blank.");
        }

        if (request.Remote.StartsWith('-'))
        {
            return AgentError.Validation($"Remote '{request.Remote}' must not start with '-'.");
        }

        if (string.IsNullOrWhiteSpace(request.DefaultBranch))
        {
            return AgentError.Validation("DefaultBranch must not be blank.");
        }

        if (string.IsNullOrWhiteSpace(request.Branch))
        {
            return AgentError.Validation("Branch must not be blank.");
        }

        if (request.Branch.StartsWith('-'))
        {
            return AgentError.Validation($"Branch '{request.Branch}' must not start with '-'.");
        }

        return null;
    }

    private static bool IsValidRepositoryName(string repository) =>
        !string.IsNullOrWhiteSpace(repository)
        && repository is not ("." or "..")
        && !repository.Contains('/')
        && !repository.Contains('\\')
        && !repository.Contains('\0')
        && !repository.Contains(':');


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

    private string MirrorPath(string repository) => Path.Combine(_dataRoot, "mirrors", repository);

    private string WorktreeRoot(Guid runId) => Path.Combine(RunsDirectory, runId.ToString());

    private string SidecarPath(Guid runId) => Path.Combine(RunsDirectory, runId.ToString() + SidecarSuffix);

    /// <summary>
    /// A temp path unique to one write, so two writers never share one, and named so it never matches
    /// <see cref="SidecarSuffix"/>: nothing reads a temp file as a record.
    /// </summary>
    private string PendingSidecarPath(Guid runId) =>
        Path.Combine(RunsDirectory, $".{runId:N}.{Guid.NewGuid():N}{PendingSidecarSuffix}");

    /// <summary>
    /// Serialises git on one repository's mirror: first within this process, through a <see cref="SemaphoreSlim"/>,
    /// so this process's own callers queue without polling, then across processes, through a
    /// <see cref="CrossProcessFileLock"/> on <c>locks/mirrors/&lt;repository&gt;.lock</c>. Dispose the lease to release both.
    /// </summary>
    private async Task<MirrorLease> LockRepositoryAsync(string repository, CancellationToken ct)
    {
        var gate = _repositoryLocks.GetOrAdd(repository, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        var leased = false;
        try
        {
            var file = await CrossProcessFileLock.AcquireAsync(Path.Combine(LocksDirectory, "mirrors", repository + ".lock"), ct).ConfigureAwait(false);
            leased = true;
            return new MirrorLease(gate, file);
        }
        finally
        {
            if (!leased)
            {
                gate.Release();
            }
        }
    }

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

    /// <summary>
    /// The length of the fixed part of <see cref="TempCloneDirectoryName"/> before the repository name: the
    /// <c>.tmp-</c> prefix plus a <see cref="Guid.ToString(string?)"/> <c>"N"</c> value, which is always exactly 32
    /// hex digits, plus the separator before the repository name.
    /// </summary>
    private const int TempCloneDirectoryRepositoryOffset = 5 + 32 + 1;

    /// <summary>
    /// A unique temporary clone directory name for <paramref name="repository"/>, carrying the repository so a
    /// later sweep can tell which repository's stale directories are whose without reaching across repositories
    /// sharing the same <c>mirrors</c> directory. The GUID comes first, in fixed-width hex, so
    /// <see cref="IsTempCloneDirectoryFor"/> can find where it ends and the repository name begins without
    /// guessing at a separator a repository name — which may itself contain <c>-</c> — could also produce.
    /// </summary>
    private static string TempCloneDirectoryName(string repository) => $".tmp-{Guid.NewGuid():N}-{repository}";

    /// <summary>
    /// Deletes <c>.tmp-*</c> directories under <paramref name="mirrorsDir"/> left behind by a clone of
    /// <paramref name="repository"/> that was killed before it moved its temporary directory into place — see
    /// <see cref="CloneMirrorAsync"/>, which calls this holding that repository's mirror lock, the reason it is
    /// safe: no other clone of the same repository can be running right now, so every matching directory found here
    /// belongs to one that is not. A failed delete is logged, not fatal — an operator can remove it by hand.
    /// </summary>
    private void SweepStaleCloneTempDirectories(string mirrorsDir, string repository)
    {
        if (!Directory.Exists(mirrorsDir))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(mirrorsDir, ".tmp-*"))
        {
            if (IsTempCloneDirectoryFor(Path.GetFileName(directory), repository))
            {
                TryDeleteDirectory(directory, $"sweep the stale temporary clone directory '{directory}'");
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="directoryName"/> is a <see cref="TempCloneDirectoryName"/> for exactly
    /// <paramref name="repository"/>. The GUID segment has a fixed length, so the repository name is read from a
    /// fixed offset and compared whole — never a prefix match — so repository <c>foo</c> cannot match a directory
    /// that in fact belongs to repository <c>foo-bar</c>.
    /// </summary>
    private static bool IsTempCloneDirectoryFor(string directoryName, string repository) =>
        directoryName.Length == TempCloneDirectoryRepositoryOffset + repository.Length
        && directoryName.StartsWith(".tmp-", StringComparison.Ordinal)
        && directoryName[TempCloneDirectoryRepositoryOffset - 1] == '-'
        && string.CompareOrdinal(directoryName, TempCloneDirectoryRepositoryOffset, repository, 0, repository.Length) == 0;

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

    /// <summary>Holds one repository's in-process and cross-process locks until disposed.</summary>
    private sealed class MirrorLease(SemaphoreSlim gate, FileStream file) : IDisposable
    {
        public void Dispose()
        {
            file.Dispose();
            gate.Release();
        }
    }

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

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information, Message = "Another process cloned the mirror at '{Mirror}' first; validating and using it.")]
    private static partial void LogFirstCloneRaceLost(ILogger logger, string mirror);
}
