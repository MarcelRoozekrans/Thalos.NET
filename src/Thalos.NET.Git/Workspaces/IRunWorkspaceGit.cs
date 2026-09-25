using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Git.Workspaces;

/// <summary>
/// Git operations on a run's own worktree, with the remote and branch taken from its workspace record, never from
/// a caller-supplied string. Every implementation runs git as a real child process through <see cref="GitCli"/>,
/// never LibGit2Sharp and never <see cref="Task.Run{TResult}(Func{TResult})"/> to fake async over a synchronous API
/// (ruling R23) — see <see cref="GitCliRunWorkspaceGit"/>.
/// </summary>
public interface IRunWorkspaceGit
{
    /// <summary>
    /// Stages <see cref="GitCommitRequest.Paths"/> (or everything), unstages
    /// <see cref="GitCommitRequest.ExcludePaths"/>, and commits. Nothing staged gives
    /// <see cref="GitCommitResult.Created"/> <see langword="false"/>, and no commit.
    /// </summary>
    /// <param name="workspace">The run's worktree; the commit runs in <see cref="RunWorkspace.Root"/>.</param>
    /// <param name="request">The commit's message, identity and path scope.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<Result<GitCommitResult, AgentError>> CommitAsync(RunWorkspace workspace, GitCommitRequest request, CancellationToken ct);

    /// <summary>
    /// Every file changed between the merge base of <see cref="RunWorkspace.BaseRef"/> and <c>HEAD</c>, and
    /// <c>HEAD</c>, with line counts.
    /// </summary>
    /// <param name="workspace">The run's worktree.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<Result<IReadOnlyList<GitFileChange>, AgentError>> DiffStatAsync(RunWorkspace workspace, CancellationToken ct);

    /// <summary>
    /// Pushes <c>HEAD</c> to <c>refs/heads/&lt;workspace.Branch&gt;</c> on <c>origin</c>, the run's allow-listed
    /// remote. Pushing what is already there succeeds and changes nothing.
    /// </summary>
    /// <param name="workspace">The run's worktree.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<UnitResult<AgentError>> PushAsync(RunWorkspace workspace, CancellationToken ct);
}
