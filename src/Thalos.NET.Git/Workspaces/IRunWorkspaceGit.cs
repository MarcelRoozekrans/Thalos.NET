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
    /// Stages <see cref="GitCommitRequest.Paths"/> (or everything, only when it is <see langword="null"/>), unstages
    /// <see cref="GitCommitRequest.ExcludePaths"/>, and commits. Nothing staged gives
    /// <see cref="GitCommitResult.Created"/> <see langword="false"/>, no commit, and <c>HEAD</c>'s unchanged sha.
    /// A <see cref="GitCommitRequest.Paths"/> or <see cref="GitCommitRequest.ExcludePaths"/> entry that is neither
    /// on disk nor tracked contributes nothing and is not an error; when every <see cref="GitCommitRequest.Paths"/>
    /// entry is like that, or the list is empty, nothing is staged — never everything. A tracked path deleted from
    /// disk is not absent: its deletion is staged. Any other git failure still fails the call.
    /// With <see cref="GitCommitRequest.CommitStagedIndex"/>, nothing is staged from disk: the index is committed as it
    /// stands, less <see cref="GitCommitRequest.ExcludePaths"/>, and <see cref="GitCommitRequest.Paths"/> must be
    /// <see langword="null"/>, or the call fails with <see cref="AgentErrorCode.Validation"/>. The run's change in a
    /// sandboxed run's publish worktree must be committed this way; a file the host writes there afterwards is
    /// committed with <see cref="GitCommitRequest.Paths"/>.
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
    /// Refuses outright when <see cref="RunWorkspace.Branch"/> equals <see cref="RunWorkspace.DefaultBranch"/>, and
    /// otherwise verifies the worktree's checked-out branch really is <c>refs/heads/&lt;Branch&gt;</c> before
    /// pushing anything. Pushes the explicit two-sided refspec <c>refs/heads/&lt;Branch&gt;:refs/heads/&lt;Branch&gt;</c>
    /// straight to <see cref="RunWorkspace.Remote"/> — never through a named <c>origin</c> remote, and never any
    /// other ref. Pushing what is already there succeeds and changes nothing.
    /// </summary>
    /// <param name="workspace">The run's worktree.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<UnitResult<AgentError>> PushAsync(RunWorkspace workspace, CancellationToken ct);
}
