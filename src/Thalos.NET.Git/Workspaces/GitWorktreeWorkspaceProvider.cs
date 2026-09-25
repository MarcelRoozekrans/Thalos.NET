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
/// <b>The per-repository lock.</b> A <see cref="SemaphoreSlim"/>, one per <see cref="RunWorkspaceRequest.Repository"/>,
/// is held across every git call this provider makes against that repository's mirror — clone, fetch, worktree
/// add, worktree remove, and branch delete — because two runs sharing a repository must not clone or fetch the one
/// mirror directory at the same time. No test exercises the race directly (a red for it would depend on git losing
/// a timing race that cannot be forced, so none could be verified — ruling R16), but the lock stays: the hazard it
/// prevents is real even though it cannot be demonstrated with a deterministic test.
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
        var (extraConfig, secret) = CredentialConfig(request.Remote);

        var gate = LockFor(request.Repository);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var prepared = await PrepareMirrorAsync(mirror, request.Remote, extraConfig, secret, ct).ConfigureAwait(false);
            if (prepared.IsFailure)
            {
                return Result<RunWorkspace, AgentError>.Failure(prepared.Error);
            }

            var added = await AddWorktreeAsync(mirror, root, request, ct).ConfigureAwait(false);
            if (added.IsFailure)
            {
                return Result<RunWorkspace, AgentError>.Failure(added.Error);
            }

            var createdAt = clock.GetUtcNow();
            var solution = ResolveSolution(root, request.Solution);
            if (solution.IsFailure)
            {
                await CleanupCreatedWorktreeAsync(mirror, root, request.Branch, ct).ConfigureAwait(false);
                return Result<RunWorkspace, AgentError>.Failure(solution.Error);
            }

            var workspace = new RunWorkspace(request.RunId, request.Repository, request.Remote, request.DefaultBranch, request.Branch, root, solution.Value)
            {
                CreatedAt = createdAt,
            };

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

        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonSerializer.DeserializeAsync(stream, GitWorkspaceJsonContext.Default.RunWorkspace, ct).ConfigureAwait(false);
        }
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
            var stream = File.OpenRead(file);
            await using (stream.ConfigureAwait(false))
            {
                if (await JsonSerializer.DeserializeAsync(stream, GitWorkspaceJsonContext.Default.RunWorkspace, ct).ConfigureAwait(false) is { } workspace)
                {
                    workspaces.Add(workspace);
                }
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
            var (removed, branchDeleted) = await RemoveWorktreeAndBranchAsync(mirror, workspace.Root, workspace.Branch, ct).ConfigureAwait(false);
            if (!removed.Succeeded)
            {
                result = UnitResult<AgentError>.Failure(GitFailure("git worktree remove failed.", removed.StdErr, secret: null));
            }
            else if (branchDeleted is { Succeeded: false })
            {
                result = UnitResult<AgentError>.Failure(GitFailure("git branch -D failed.", branchDeleted.Value.StdErr, secret: null));
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

    private async Task<UnitResult<AgentError>> PrepareMirrorAsync(string mirror, string remote, IReadOnlyList<string>? extraConfig, string? secret, CancellationToken ct)
    {
        var mirrorExisted = Directory.Exists(mirror);
        if (!mirrorExisted)
        {
            var mirrorsDir = Path.Combine(options.DataRoot, "mirrors");
            Directory.CreateDirectory(mirrorsDir);

            var cloned = await _git.RunAsync(mirrorsDir, ["clone", "--bare", "--", remote, mirror], extraConfig, ct).ConfigureAwait(false);
            if (!cloned.Succeeded)
            {
                TryDeleteDirectory(mirror, "remove the partially cloned mirror");
                return UnitResult<AgentError>.Failure(GitFailure("git clone --bare failed.", cloned.StdErr, secret));
            }

            var fetchSpec = await _git.RunAsync(mirror, ["config", "remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*"], null, ct).ConfigureAwait(false);
            if (!fetchSpec.Succeeded)
            {
                TryDeleteDirectory(mirror, "remove the partially cloned mirror");
                return UnitResult<AgentError>.Failure(GitFailure("git config remote.origin.fetch failed.", fetchSpec.StdErr, secret));
            }

            // Persisted on the mirror in addition to being passed again on every worktree checkout below — see the
            // class remarks for why symlinks must never reach a workspace this provider creates.
            var noSymlinks = await _git.RunAsync(mirror, ["config", "core.symlinks", "false"], null, ct).ConfigureAwait(false);
            if (!noSymlinks.Succeeded)
            {
                TryDeleteDirectory(mirror, "remove the partially cloned mirror");
                return UnitResult<AgentError>.Failure(GitFailure("git config core.symlinks failed.", noSymlinks.StdErr, secret));
            }
        }

        var fetched = await _git.RunAsync(mirror, ["fetch", "origin", "--prune"], extraConfig, ct).ConfigureAwait(false);
        if (!fetched.Succeeded)
        {
            if (!mirrorExisted)
            {
                TryDeleteDirectory(mirror, "remove the partially cloned mirror");
            }

            return UnitResult<AgentError>.Failure(GitFailure("git fetch failed.", fetched.StdErr, secret));
        }

        return UnitResult<AgentError>.Success();
    }

    private async Task<UnitResult<AgentError>> AddWorktreeAsync(string mirror, string root, RunWorkspaceRequest request, CancellationToken ct)
    {
        var added = await _git.RunAsync(
            mirror,
            ["worktree", "add", "-b", request.Branch, "--", root, $"origin/{request.DefaultBranch}"],
            ["core.symlinks=false"],
            ct).ConfigureAwait(false);

        if (added.Succeeded)
        {
            return UnitResult<AgentError>.Success();
        }

        TryDeleteDirectory(root, "remove the partially created worktree");

        // git may have registered the worktree in the mirror's administrative files before the checkout itself
        // failed; prune drops that stale registration so a later create for the same repository is not blocked by it.
        var pruned = await _git.RunAsync(mirror, ["worktree", "prune"], null, ct).ConfigureAwait(false);
        if (!pruned.Succeeded)
        {
            LogCleanupFailed(logger, "prune the stale worktree registration", FirstLine(pruned.StdErr));
        }

        return UnitResult<AgentError>.Failure(GitFailure("git worktree add failed.", added.StdErr, secret: null));
    }

    private async Task CleanupCreatedWorktreeAsync(string mirror, string root, string branch, CancellationToken ct)
    {
        var (removed, branchDeleted) = await RemoveWorktreeAndBranchAsync(mirror, root, branch, ct).ConfigureAwait(false);
        if (!removed.Succeeded)
        {
            LogCleanupFailed(logger, "remove the worktree", FirstLine(removed.StdErr));
            TryDeleteDirectory(root, "remove the worktree directory directly, after git worktree remove failed");
            return;
        }

        if (branchDeleted is { Succeeded: false })
        {
            LogCleanupFailed(logger, "delete the run branch", FirstLine(branchDeleted.Value.StdErr));
        }
    }

    private async Task<(GitCliResult Removed, GitCliResult? BranchDeleted)> RemoveWorktreeAndBranchAsync(string mirror, string root, string branch, CancellationToken ct)
    {
        var removed = await _git.RunAsync(mirror, ["worktree", "remove", "--force", "--", root], null, ct).ConfigureAwait(false);
        if (!removed.Succeeded)
        {
            return (removed, null);
        }

        var branchDeleted = await _git.RunAsync(mirror, ["branch", "-D", "--", branch], null, ct).ConfigureAwait(false);
        return (removed, branchDeleted);
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

    private (IReadOnlyList<string>? ExtraConfig, string? Secret) CredentialConfig(string remoteUrl)
    {
        if (credentials?.GetCredentials(remoteUrl) is not { } creds)
        {
            return (null, null);
        }

        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{creds.Username}:{creds.Password}"));
        return ([$"http.extraHeader=AUTHORIZATION: basic {token}"], token);
    }

    private static AgentError GitFailure(string message, string stdErr, string? secret) =>
        AgentError.GitOperationFailed(message, FirstLine(Scrub(stdErr, secret)));

    private static string Scrub(string text, string? secret) =>
        secret is null ? text : text.Replace(secret, "***", StringComparison.Ordinal);

    private static string FirstLine(string text)
    {
        var trimmed = text.AsSpan().Trim();
        var newline = trimmed.IndexOfAny('\r', '\n');
        return (newline < 0 ? trimmed : trimmed[..newline]).ToString();
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
}
