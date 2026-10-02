namespace Thalos.Git;

/// <summary>Request to <see cref="Workspaces.IRunWorkspaceGit.CommitAsync"/>.</summary>
public sealed record GitCommitRequest
{
    /// <summary>The commit message.</summary>
    public required string Message { get; init; }

    /// <summary>
    /// The commit's author and committer identity — the same identity is recorded as both, as
    /// <see cref="GitAuthor"/> already documents. Required (ruling R27: optional only when absence is a supported
    /// configuration, and it is not here): <see cref="Workspaces.GitCli"/>'s host-configuration isolation means no
    /// <c>user.name</c>/<c>user.email</c> can ever be picked up from git config instead, so there is no fallback a
    /// caller could rely on, and every caller — including a test — must supply its own.
    /// </summary>
    public required GitAuthor Author { get; init; }

    /// <summary>
    /// Repository-relative paths, confined to the workspace root, to stage. Only <see langword="null"/> stages every
    /// change in the worktree; an empty list names nothing, so nothing is staged and no commit is made. An entry that
    /// is neither on disk nor tracked contributes nothing and is not an error; if every entry is like that, nothing
    /// is staged and no commit is made — a list never widens to every change. A tracked entry deleted from disk
    /// stages its deletion.
    /// </summary>
    public IReadOnlyList<string>? Paths { get; init; }

    /// <summary>
    /// Repository-relative paths, confined to the workspace root, never staged by this commit — unstaged after
    /// <see cref="Paths"/> is applied, or, with <see cref="CommitStagedIndex"/>, from the index as it stands. Unstaging
    /// resets the path's index entry to <c>HEAD</c>, so neither a new file nor a change to a tracked one under it is
    /// committed; the file on disk is left as it is. An entry that is neither on disk nor tracked excludes nothing
    /// and is not an error.
    /// </summary>
    public IReadOnlyList<string>? ExcludePaths { get; init; }

    /// <summary>
    /// <see langword="true"/> commits the index exactly as it stands: the index is not reset to <c>HEAD</c> and
    /// nothing is added from disk, so only what is already staged is committed. <see cref="ExcludePaths"/> is still
    /// unstaged first, so an excluded path is never committed. <see cref="Paths"/> must be <see langword="null"/>; a
    /// list, empty included, is refused with <see cref="AgentErrorCode.Validation"/> before anything is staged. With
    /// nothing staged, no commit is made, the same as without this option.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Use it for a worktree whose change a trusted step has already staged and checked. A sandboxed run's publish
    /// worktree, the one <c>IRunWorkspaceHandoff.CheckoutForPublishAsync</c> returns in sandbox mode, holds the run's
    /// patch as <see cref="Workspaces.GitPatchApplier"/> applied it with <c>git apply --index</c>, and that index is
    /// what the protected-path check passed. Restaging from disk would drop an added file the worktree's <c>.gitignore</c> matches
    /// and, under <c>core.fileMode=false</c> (the Windows default), a mode change, so the commit would differ from what
    /// was checked.
    /// </para>
    /// <para>
    /// <see langword="false"/>, the default, resets the index to <c>HEAD</c> and stages from disk with
    /// <c>git add -A</c>, scoped by <see cref="Paths"/>: what a local worktree needs, whose agent writes files that are
    /// never staged.
    /// </para>
    /// </remarks>
    public bool CommitStagedIndex { get; init; }
}
