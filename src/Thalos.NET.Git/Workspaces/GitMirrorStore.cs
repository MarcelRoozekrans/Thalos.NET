using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Git.Workspaces;

/// <summary>A prepared bare mirror: the repository name it is keyed by and its directory under the data root.</summary>
/// <param name="Repository">The repository name, a single path segment.</param>
/// <param name="Directory">The mirror's bare repository directory.</param>
public sealed record GitMirror(string Repository, string Directory);

/// <summary>
/// The trusted side's bare git mirror per repository: cloning, fetching, validating its config against
/// <see cref="MirrorConfigSurface"/>, serialising git on it across processes, writing bundles from it and reading
/// files at a pinned commit. <see cref="GitWorktreeWorkspaceProvider"/> delegates its mirror handling here, and a
/// sandbox provider uses it to hand a run its source without ever giving the run the mirror itself.
/// </summary>
/// <remarks>
/// Every git call goes through <see cref="GitCli"/>, so the isolation, protocol and credential rules in its remarks
/// apply unchanged. The mirror handling is the code that previously lived in the provider, moved without change;
/// see <see cref="GitWorktreeWorkspaceProvider"/>'s remarks for the first-clone, validation and lock rationale.
/// </remarks>
/// <param name="options">Where mirrors and locks live, and how the git child process is run.</param>
/// <param name="logger">Required: every host has one.</param>
/// <param name="credentials">
/// Supplies HTTP(S) credentials per remote. <see langword="null"/> means every remote is fetched anonymously.
/// </param>
public sealed partial class GitMirrorStore(GitWorkspaceOptions options, ILogger<GitMirrorStore> logger, IGitCredentialSource? credentials = null)
{
    private readonly GitCli _git = new(options);
    private readonly string _dataRoot = Path.GetFullPath(options.DataRoot);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _repositoryLocks = new(StringComparer.Ordinal);

    /// <summary>Clones the bare mirror if absent, retargets origin, fetches, validates its config. Serialised per repository across processes.</summary>
    /// <param name="repository">The repository name; one path segment, naming the mirror directory.</param>
    /// <param name="remote">The remote to clone from and fetch.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Result<GitMirror, AgentError>> PrepareAsync(string repository, string remote, CancellationToken ct)
    {
        if (ValidateRepositoryAndRemote(repository, remote) is { } invalid)
        {
            return Result<GitMirror, AgentError>.Failure(invalid);
        }

        var mirror = MirrorPath(repository);
        var (secretConfig, secret) = CredentialConfig(remote);
        using (await LockRepositoryAsync(repository, ct).ConfigureAwait(false))
        {
            var prepared = await PrepareMirrorAsync(mirror, remote, secretConfig, secret, ct).ConfigureAwait(false);
            return prepared.IsFailure
                ? Result<GitMirror, AgentError>.Failure(prepared.Error)
                : Result<GitMirror, AgentError>.Success(new GitMirror(repository, mirror));
        }
    }

    /// <summary>The full sha of <c>refs/remotes/origin/&lt;branch&gt;</c>.</summary>
    /// <param name="mirror">A mirror from <see cref="PrepareAsync"/>.</param>
    /// <param name="branch">The branch name.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Result<string, AgentError>> ResolveBranchAsync(GitMirror mirror, string branch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        if (string.IsNullOrWhiteSpace(branch))
        {
            return Result<string, AgentError>.Failure(AgentError.Validation("Branch must not be blank."));
        }

        var resolved = await _git.RunAsync(mirror.Directory, ["rev-parse", "--verify", $"refs/remotes/origin/{branch}^{{commit}}"], null, null, ct).ConfigureAwait(false);
        return resolved.Succeeded
            ? Result<string, AgentError>.Success(resolved.StdOut.Trim())
            : Result<string, AgentError>.Failure(GitFailure($"git rev-parse of branch '{branch}' failed.", resolved, secret: null));
    }

    /// <summary>
    /// Writes a bundle from which <paramref name="commit"/> can be cloned and checked out.
    /// </summary>
    /// <remarks>
    /// <c>git bundle</c> records ref names and cannot rename them, so there is no branch parameter: the commit is
    /// bundled under a temporary <c>refs/heads/thalos-bundle/&lt;guid&gt;</c> ref, created and deleted under the
    /// mirror's lock, and the importer clones the bundle and checks out <paramref name="commit"/> by sha. The ref is
    /// under <c>refs/heads/</c>, not a private namespace, because <c>git clone</c> of a bundle fetches only branch
    /// refs: a commit bundled under any other ref arrives in the clone without its objects, and the checkout fails.
    /// </remarks>
    /// <param name="mirror">A mirror from <see cref="PrepareAsync"/>.</param>
    /// <param name="commit">The full 40-character sha to bundle.</param>
    /// <param name="bundlePath">Where to write the bundle file.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<UnitResult<AgentError>> CreateBundleAsync(GitMirror mirror, string commit, string bundlePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        if (!IsFullSha(commit))
        {
            return UnitResult<AgentError>.Failure(AgentError.Validation("The commit must be a full 40-character sha."));
        }

        if (string.IsNullOrWhiteSpace(bundlePath))
        {
            return UnitResult<AgentError>.Failure(AgentError.Validation("The bundle path must not be blank."));
        }

        var temporaryRef = $"refs/heads/thalos-bundle/{Guid.NewGuid():N}";
        using (await LockRepositoryAsync(mirror.Repository, ct).ConfigureAwait(false))
        {
            var created = await _git.RunAsync(mirror.Directory, ["update-ref", temporaryRef, commit], null, null, ct).ConfigureAwait(false);
            if (!created.Succeeded)
            {
                return UnitResult<AgentError>.Failure(GitFailure("git update-ref of the bundle ref failed.", created, secret: null));
            }

            try
            {
                var bundled = await _git.RunAsync(mirror.Directory, ["bundle", "create", bundlePath, temporaryRef], null, null, ct).ConfigureAwait(false);
                return bundled.Succeeded
                    ? UnitResult<AgentError>.Success()
                    : UnitResult<AgentError>.Failure(GitFailure("git bundle create failed.", bundled, secret: null));
            }
            finally
            {
                // In every case, cancellation included: a leftover ref would pin the objects forever.
                var deleted = await _git.RunAsync(mirror.Directory, ["update-ref", "-d", temporaryRef], null, null, CancellationToken.None).ConfigureAwait(false);
                if (!deleted.Succeeded)
                {
                    LogCleanupFailed(logger, "delete the temporary bundle ref", ExtractErrorDetail(deleted.StdErr));
                }
            }
        }
    }

    /// <summary>
    /// The text of <paramref name="relativePath"/> at <paramref name="commit"/>, or <see langword="null"/> when it
    /// is absent or is not a file.
    /// </summary>
    /// <remarks>
    /// The path is checked lexically by <see cref="RepoRelativePath.Validate"/> before any git process starts. A
    /// missing object and a non-blob object, such as a directory's tree, both read as absent. The read is
    /// <c>cat-file blob</c>, not <c>show</c>, so no textconv filter runs.
    /// </remarks>
    /// <param name="mirror">A mirror from <see cref="PrepareAsync"/>.</param>
    /// <param name="commit">The full 40-character sha to read at.</param>
    /// <param name="relativePath">A repository-relative path, forward slashes.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Result<string?, AgentError>> ReadFileAsync(GitMirror mirror, string commit, string relativePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        if (RepoRelativePath.Validate(relativePath) is { } invalid)
        {
            return Result<string?, AgentError>.Failure(invalid);
        }

        if (!IsFullSha(commit))
        {
            return Result<string?, AgentError>.Failure(AgentError.Validation("The commit must be a full 40-character sha."));
        }

        var spec = $"{commit}:{relativePath.Replace('\\', '/')}";
        var kind = await _git.RunAsync(mirror.Directory, ["cat-file", "-t", spec], null, null, ct).ConfigureAwait(false);
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

        var shown = await _git.RunAsync(mirror.Directory, ["cat-file", "blob", spec], null, null, ct).ConfigureAwait(false);
        return shown.Succeeded
            ? Result<string?, AgentError>.Success(shown.StdOut)
            : Result<string?, AgentError>.Failure(GitFailure("git cat-file of the file failed.", shown, secret: null));
    }

    /// <summary>The directory of <paramref name="repository"/>'s mirror under the data root.</summary>
    internal string MirrorPath(string repository) => Path.Combine(_dataRoot, "mirrors", repository);

    /// <summary>Every lock file lives here: no repository name can reach it.</summary>
    private string LocksDirectory => Path.Combine(_dataRoot, "locks");

    internal static bool IsValidRepositoryName(string repository) =>
        !string.IsNullOrWhiteSpace(repository)
        && repository is not ("." or "..")
        && !repository.Contains('/')
        && !repository.Contains('\\')
        && !repository.Contains('\0')
        && !repository.Contains(':');

    private static AgentError? ValidateRepositoryAndRemote(string repository, string remote)
    {
        if (!IsValidRepositoryName(repository))
        {
            return AgentError.Validation($"Repository '{repository}' is not a valid mirror directory name.");
        }

        if (string.IsNullOrWhiteSpace(remote))
        {
            return AgentError.Validation("Remote must not be blank.");
        }

        return remote.StartsWith('-') ? AgentError.Validation($"Remote '{remote}' must not start with '-'.") : null;
    }

    /// <summary>Whether <paramref name="value"/> is exactly 40 lowercase hex digits, a full sha-1 commit id.</summary>
    internal static bool IsFullSha(string value)
    {
        const int ShaLength = 40;
        if (value.Length != ShaLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigitLower(c))
            {
                return false;
            }
        }

        return true;
    }

    // ---------- moved from GitWorktreeWorkspaceProvider ----------

    internal async Task<UnitResult<AgentError>> PrepareMirrorAsync(string mirror, string remote, IReadOnlyList<(string Key, string Value)>? secretConfig, string? secret, CancellationToken ct)
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

    /// <summary>
    /// Serialises git on one repository's mirror: first within this process, through a <see cref="SemaphoreSlim"/>,
    /// so this process's own callers queue without polling, then across processes, through a
    /// <see cref="CrossProcessFileLock"/> on <c>locks/mirrors/&lt;repository&gt;.lock</c>. Dispose the lease to release both.
    /// </summary>
    internal async Task<MirrorLease> LockRepositoryAsync(string repository, CancellationToken ct)
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

    /// <summary>Holds one repository's in-process and cross-process locks until disposed.</summary>
    internal sealed class MirrorLease(SemaphoreSlim gate, FileStream file) : IDisposable
    {
        public void Dispose()
        {
            file.Dispose();
            gate.Release();
        }
    }

    internal (IReadOnlyList<(string Key, string Value)>? SecretConfig, string? Secret) CredentialConfig(string remoteUrl)
    {
        if (credentials?.GetCredentials(remoteUrl) is not { } creds)
        {
            return (null, null);
        }

        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{creds.Username}:{creds.Password}"));
        return ([("http.extraHeader", $"AUTHORIZATION: basic {token}")], token);
    }

    internal static AgentError GitFailure(string message, GitCliResult result, string? secret)
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

    [LoggerMessage(EventId = 1100, Level = LogLevel.Warning, Message = "Could not {What}: {Error}")]
    private static partial void LogCleanupFailed(ILogger logger, string what, string error);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Information, Message = "Another process cloned the mirror at '{Mirror}' first; validating and using it.")]
    private static partial void LogFirstCloneRaceLost(ILogger logger, string mirror);
}
