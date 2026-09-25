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
/// <b>A first clone is atomic.</b> The mirror is cloned into a temporary directory beside its final location and
/// moved into place only once the clone and its follow-up config calls all succeed; a failure at any point leaves
/// nothing at the mirror's real path. An existing mirror is validated — bare, with <c>remote.origin.fetch</c> set —
/// before it is trusted; an invalid one is deleted and re-cloned rather than used as-is.
/// </para>
/// <para>
/// <b>A run's root and sidecar are checked before anything is created.</b> <see cref="CreateAsync"/> refuses, with
/// a failure <see cref="Result{T,E}"/>, a request whose run already has a worktree directory or a sidecar record —
/// a second create for a run that already has a live workspace never touches it. A provisional sidecar (the same
/// <see cref="RunWorkspace"/>, with <see cref="RunWorkspace.SolutionPath"/> not yet resolved) is written before
/// <c>git worktree add</c> runs, so a crash between the worktree's creation and the create's own completion still
/// leaves a sidecar a sweeper can find and act on, rather than an orphan directory with no record at all.
/// </para>
/// <para>
/// <b>The per-repository lock.</b> A <see cref="SemaphoreSlim"/>, one per <see cref="RunWorkspaceRequest.Repository"/>,
/// is held across every git call this provider makes against that repository's mirror — clone, fetch, worktree
/// add, worktree remove, and branch delete — because two runs sharing a repository must not clone or fetch the one
/// mirror directory at the same time. No test exercises the race directly (a red for it would depend on git losing
/// a timing race that cannot be forced, so none could be verified — ruling R16), but the lock stays: the hazard it
/// prevents is real even though it cannot be demonstrated with a deterministic test. The lock is scoped to this
/// process only; it does nothing for two separate host processes racing the same mirror. Git's own lock files
/// (<c>index.lock</c>, the per-worktree administrative locks under <c>.git/worktrees/&lt;name&gt;</c>, and so on)
/// are what prevent corruption across processes — this provider relies on them for that, rather than reimplementing
/// cross-process locking itself.
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

        if (Directory.Exists(root) || File.Exists(SidecarPath(request.RunId)))
        {
            return Result<RunWorkspace, AgentError>.Failure(
                AgentError.Validation($"A workspace for run '{request.RunId}' already exists."));
        }

        var (secretConfig, secret) = CredentialConfig(request.Remote);

        var gate = LockFor(request.Repository);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var prepared = await PrepareMirrorAsync(mirror, request.Remote, secretConfig, secret, ct).ConfigureAwait(false);
            if (prepared.IsFailure)
            {
                return Result<RunWorkspace, AgentError>.Failure(prepared.Error);
            }

            var createdAt = clock.GetUtcNow();
            var provisional = new RunWorkspace(request.RunId, request.Repository, request.Remote, request.DefaultBranch, request.Branch, root, SolutionPath: null)
            {
                CreatedAt = createdAt,
            };
            await WriteSidecarAsync(provisional, ct).ConfigureAwait(false);

            var added = await AddWorktreeAsync(mirror, root, request, ct).ConfigureAwait(false);
            if (added.IsFailure)
            {
                DeleteSidecar(request.RunId);
                return Result<RunWorkspace, AgentError>.Failure(added.Error);
            }

            var solution = ResolveSolution(root, request.Solution);
            if (solution.IsFailure)
            {
                await CleanupCreatedWorktreeAsync(mirror, root, request.Branch, ct).ConfigureAwait(false);
                DeleteSidecar(request.RunId);
                return Result<RunWorkspace, AgentError>.Failure(solution.Error);
            }

            var workspace = provisional with { SolutionPath = solution.Value };
            await WriteSidecarAsync(workspace, ct).ConfigureAwait(false);
            await NotifyObserversAsync(workspace, removing: false, ct).ConfigureAwait(false);
            return Result<RunWorkspace, AgentError>.Success(workspace);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<RunWorkspace?> FindAsync(Guid runId, CancellationToken ct)
    {
        var path = SidecarPath(runId);
        if (!File.Exists(path))
        {
            return null;
        }

        return await ReadSidecarAsync(path, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<RunWorkspace>> ListAsync(CancellationToken ct)
    {
        var runsDir = Path.Combine(options.DataRoot, "runs");
        if (!Directory.Exists(runsDir))
        {
            return [];
        }

        var workspaces = new List<RunWorkspace>();
        foreach (var file in Directory.EnumerateFiles(runsDir, "*.workspace.json"))
        {
            ct.ThrowIfCancellationRequested();

            RunWorkspace? workspace;
            try
            {
                workspace = await ReadSidecarAsync(file, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // A sidecar the sweeper cannot read is not a reason to abandon the sweep for every other run —
                // skip it and log, rather than letting one bad file abort ListAsync for everything else.
                LogUnreadableSidecar(logger, file, ex.Message);
                continue;
            }

            if (workspace is not null)
            {
                workspaces.Add(workspace);
            }
        }

        return workspaces;
    }

    /// <inheritdoc />
    public async ValueTask<UnitResult<AgentError>> RemoveAsync(Guid runId, CancellationToken ct)
    {
        var workspace = await FindAsync(runId, ct).ConfigureAwait(false);
        if (workspace is null)
        {
            // Removing an already-removed (or never-created) workspace succeeds: there is nothing left to do.
            return UnitResult<AgentError>.Success();
        }

        await NotifyObserversAsync(workspace, removing: true, ct).ConfigureAwait(false);

        var mirror = MirrorPath(workspace.Repository);
        var gate = LockFor(workspace.Repository);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        UnitResult<AgentError> result;
        try
        {
            var (removed, pruned, branchDeleted) = await RemoveWorktreeAndBranchAsync(mirror, workspace.Root, workspace.Branch, ct).ConfigureAwait(false);
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

                result = UnitResult<AgentError>.Failure(GitFailure("git worktree remove failed.", removed, secret: null));
            }
            else if (!branchDeleted.Succeeded)
            {
                result = UnitResult<AgentError>.Failure(GitFailure("git branch -D failed.", branchDeleted, secret: null));
            }
            else
            {
                result = UnitResult<AgentError>.Success();
            }
        }
        finally
        {
            gate.Release();
        }

        if (result.IsSuccess)
        {
            DeleteSidecar(runId);
        }

        return result;
    }

    // ---------- mirror + worktree ----------

    private async Task<UnitResult<AgentError>> PrepareMirrorAsync(string mirror, string remote, IReadOnlyList<(string Key, string Value)>? secretConfig, string? secret, CancellationToken ct)
    {
        if (Directory.Exists(mirror) && !await ValidateMirrorAsync(mirror, ct).ConfigureAwait(false))
        {
            TryDeleteDirectory(mirror, "remove the invalid mirror before re-cloning");
        }

        if (!Directory.Exists(mirror))
        {
            var cloned = await CloneMirrorAsync(mirror, remote, secretConfig, secret, ct).ConfigureAwait(false);
            if (cloned.IsFailure)
            {
                return cloned;
            }
        }

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

        return UnitResult<AgentError>.Success();
    }

    private async Task<UnitResult<AgentError>> CloneMirrorAsync(string mirror, string remote, IReadOnlyList<(string Key, string Value)>? secretConfig, string? secret, CancellationToken ct)
    {
        var mirrorsDir = Path.Combine(options.DataRoot, "mirrors");
        Directory.CreateDirectory(mirrorsDir);
        var temp = Path.Combine(mirrorsDir, ".tmp-" + Guid.NewGuid().ToString("N"));
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

            Directory.Move(temp, mirror);
            return UnitResult<AgentError>.Success();
        }
        finally
        {
            // Runs whether the clone succeeded (the temp directory has already been moved away and this is a
            // no-op) or failed at any step (nothing is left half-cloned at either the temp or the real path).
            TryDeleteDirectory(temp, "remove the temporary clone directory");
        }
    }

    private async Task<bool> ValidateMirrorAsync(string mirror, CancellationToken ct)
    {
        var isBare = await _git.RunAsync(mirror, ["rev-parse", "--is-bare-repository"], null, null, ct).ConfigureAwait(false);
        if (!isBare.Succeeded || !string.Equals(isBare.StdOut.Trim(), "true", StringComparison.Ordinal))
        {
            return false;
        }

        var fetchSpec = await _git.RunAsync(mirror, ["config", "--get", "remote.origin.fetch"], null, null, ct).ConfigureAwait(false);
        return fetchSpec.Succeeded && !string.IsNullOrWhiteSpace(fetchSpec.StdOut);
    }

    private async Task<UnitResult<AgentError>> AddWorktreeAsync(string mirror, string root, RunWorkspaceRequest request, CancellationToken ct)
    {
        var added = await _git.RunAsync(
            mirror,
            ["worktree", "add", "-b", request.Branch, "--", root, $"origin/{request.DefaultBranch}"],
            ["core.symlinks=false"],
            null,
            ct).ConfigureAwait(false);

        if (added.Succeeded)
        {
            return UnitResult<AgentError>.Success();
        }

        TryDeleteDirectory(root, "remove the partially created worktree");

        // git may have registered the worktree in the mirror's administrative files before the checkout itself
        // failed; prune drops that stale registration so a later create for the same repository is not blocked by it.
        var pruned = await _git.RunAsync(mirror, ["worktree", "prune"], null, null, ct).ConfigureAwait(false);
        if (!pruned.Succeeded)
        {
            LogCleanupFailed(logger, "prune the stale worktree registration", ExtractErrorDetail(pruned.StdErr));
        }

        return UnitResult<AgentError>.Failure(GitFailure("git worktree add failed.", added, secret: null));
    }

    private async Task CleanupCreatedWorktreeAsync(string mirror, string root, string branch, CancellationToken ct)
    {
        var (removed, pruned, branchDeleted) = await RemoveWorktreeAndBranchAsync(mirror, root, branch, ct).ConfigureAwait(false);
        if (!removed.Succeeded)
        {
            LogCleanupFailed(logger, "remove the worktree", ExtractErrorDetail(removed.StdErr));
            if (pruned is { Succeeded: false } p)
            {
                LogCleanupFailed(logger, "prune the stale worktree registration", ExtractErrorDetail(p.StdErr));
            }

            TryDeleteDirectory(root, "remove the worktree directory directly, after git worktree remove failed");
        }

        if (!branchDeleted.Succeeded)
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
            catch (Exception ex) when (ex is not OperationCanceledException)
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
    /// The first line starting with <c>fatal:</c> or <c>error:</c> — git's own convention for the line that names
    /// what went wrong, buried among progress and hint lines <c>-q</c> does not suppress — or, when no line
    /// matches, the last non-empty line, which is usually the most specific one available.
    /// </summary>
    private static string ExtractErrorDetail(string stdErr)
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

    private string MirrorPath(string repository) => Path.Combine(options.DataRoot, "mirrors", repository);

    private string WorktreeRoot(Guid runId) => Path.Combine(options.DataRoot, "runs", runId.ToString());

    private string SidecarPath(Guid runId) => Path.Combine(options.DataRoot, "runs", runId.ToString() + ".workspace.json");

    private SemaphoreSlim LockFor(string repository) => _repositoryLocks.GetOrAdd(repository, static _ => new SemaphoreSlim(1, 1));

    private async Task WriteSidecarAsync(RunWorkspace workspace, CancellationToken ct)
    {
        var runsDir = Path.Combine(options.DataRoot, "runs");
        Directory.CreateDirectory(runsDir);

        var path = SidecarPath(workspace.RunId);
        var tmp = path + ".tmp";
        var stream = File.Create(tmp);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, workspace, GitWorkspaceJsonContext.Default.RunWorkspace, ct).ConfigureAwait(false);
        }

        File.Move(tmp, path, overwrite: true);
    }

    private static async Task<RunWorkspace?> ReadSidecarAsync(string path, CancellationToken ct)
    {
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonSerializer.DeserializeAsync(stream, GitWorkspaceJsonContext.Default.RunWorkspace, ct).ConfigureAwait(false);
        }
    }

    private void DeleteSidecar(Guid runId)
    {
        var path = SidecarPath(runId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private void TryDeleteDirectory(string path, string what)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCleanupFailed(logger, what, ex.Message);
        }
    }

    [LoggerMessage(EventId = 1000, Level = LogLevel.Warning, Message = "Workspace observer for run {RunId} failed during {Phase}: {Error}")]
    private static partial void LogObserverFailed(ILogger logger, Guid runId, string phase, string error);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "Could not {What}: {Error}")]
    private static partial void LogCleanupFailed(ILogger logger, string what, string error);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning, Message = "Sidecar '{Path}' could not be read and was skipped: {Error}")]
    private static partial void LogUnreadableSidecar(ILogger logger, string path, string error);
}
