using ZeroAlloc.Results;

namespace Thalos.Git;

/// <summary>
/// Local git write operations for an agent-writable working tree: create a branch, commit pending changes, and push
/// to a remote. Deliberately excludes pull-request publishing (a separate, host-specific abstraction) and any
/// destructive or history-rewriting operation (no reset, no force-anything except where explicitly documented).
/// Implementations open the repository at <c>repositoryPath</c> fresh for each call and do not cache state across
/// calls, so calls may be interleaved with the caller's own file writes or git operations between them.
/// </summary>
public interface IGitWriteService
{
    /// <summary>
    /// Creates a new local branch named <paramref name="branchName"/> from <paramref name="sourceRef"/> (a branch name,
    /// tag, or commit sha; <see langword="null"/> means the current HEAD) and checks it out, so it becomes the current
    /// branch for subsequent <see cref="CommitAsync"/> and <see cref="PushAsync"/> calls against the same
    /// <paramref name="repositoryPath"/>.
    /// </summary>
    /// <remarks>
    /// <b>If <paramref name="branchName"/> already exists locally</b>, this call fails with
    /// <see cref="AgentErrorCode.GitBranchAlreadyExists"/> and does not touch the working tree, the index, or HEAD —
    /// it never silently checks out and continues on a pre-existing branch. A caller that asked to create a new
    /// branch and got an old one instead would be operating on a state it did not expect, which risks committing
    /// on top of unrelated history. Callers that want "create, or switch if it already exists" semantics must
    /// implement that themselves (list branches first, or catch the error and switch explicitly) — this contract
    /// does not offer it.
    /// </remarks>
    /// <param name="repositoryPath">Path to the working tree root (the directory containing <c>.git</c>).</param>
    /// <param name="branchName">The branch to create. Must not already exist locally.</param>
    /// <param name="sourceRef">Branch, tag, or commit sha to branch from; <see langword="null"/> means the current HEAD.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<Result<GitBranchResult, AgentError>> CreateBranchAsync(string repositoryPath, string branchName, string? sourceRef, CancellationToken ct);

    /// <summary>
    /// Stages every pending change in the working tree — new, modified, and deleted files, respecting
    /// <c>.gitignore</c>, equivalent to <c>git add -A</c> — then commits the result on the current branch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no separate staging step in this contract: callers never need to stage first, and any staging done
    /// out of band before this call is superseded — a file staged with one set of changes and then modified again
    /// on disk is committed with its current on-disk content, not the staged snapshot.
    /// </para>
    /// <para>
    /// <b>When there is nothing to commit</b> — the working tree exactly matches HEAD after staging — this call
    /// still succeeds; it does not create an empty commit. <see cref="GitCommitResult.Created"/> is
    /// <see langword="false"/> and <see cref="GitCommitResult.Sha"/> is HEAD's unchanged sha. Callers do not need to
    /// check for pending changes before calling this.
    /// </para>
    /// </remarks>
    /// <param name="repositoryPath">Path to the working tree root (the directory containing <c>.git</c>).</param>
    /// <param name="message">The commit message.</param>
    /// <param name="author">
    /// The author and committer identity to record. When <see langword="null"/>, the repository's configured
    /// <c>user.name</c>/<c>user.email</c> (from local or global git config) is used; if neither is configured, the
    /// call fails with <see cref="AgentErrorCode.Validation"/> rather than recording an empty identity.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<Result<GitCommitResult, AgentError>> CommitAsync(string repositoryPath, string message, GitAuthor? author, CancellationToken ct);

    /// <summary>Pushes the current branch (HEAD) to a remote.</summary>
    /// <remarks>
    /// <b>When the current branch has no upstream</b> — including immediately after <see cref="CreateBranchAsync"/>,
    /// which is the normal case for a freshly created branch — this call creates one on <paramref name="remoteName"/>
    /// named after the local branch, equivalent to <c>git push -u &lt;remote&gt; &lt;branch&gt;</c>. This is the
    /// expected first push of a new branch, not an error condition, and <see cref="GitPushResult.UpstreamCreated"/>
    /// is <see langword="true"/> in that case. When an upstream already exists, this call pushes to that existing
    /// upstream and <paramref name="remoteName"/> is ignored for target selection — the established tracking
    /// configuration wins, so pass the remote you expect is already tracked (or <see langword="null"/>) rather than
    /// relying on this parameter to redirect an already-tracked branch.
    /// </remarks>
    /// <param name="repositoryPath">Path to the working tree root (the directory containing <c>.git</c>).</param>
    /// <param name="remoteName">
    /// The remote to push to when no upstream exists yet; <see langword="null"/> means <c>"origin"</c>. Ignored when
    /// the current branch already has an upstream.
    /// </param>
    /// <param name="credentials">
    /// Username/password (or PAT-as-password) for an HTTP(S) remote. <see langword="null"/> for SSH remotes using the
    /// system's SSH agent/config, or local file remotes.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<Result<GitPushResult, AgentError>> PushAsync(string repositoryPath, string? remoteName, GitCredentials? credentials, CancellationToken ct);
}
