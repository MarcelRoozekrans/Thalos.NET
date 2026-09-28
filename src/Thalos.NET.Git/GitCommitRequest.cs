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
    /// Repository-relative paths, confined to the workspace root, to stage. <see langword="null"/> stages every
    /// change in the worktree. An entry that is neither on disk nor tracked contributes nothing and is not an
    /// error; if every entry is like that, nothing is staged and no commit is made — the list never widens to
    /// every change. A tracked entry deleted from disk stages its deletion.
    /// </summary>
    public IReadOnlyList<string>? Paths { get; init; }

    /// <summary>
    /// Repository-relative paths, confined to the workspace root, never staged by this commit — unstaged after
    /// <see cref="Paths"/> is applied. An entry that is neither on disk nor tracked excludes nothing and is not an
    /// error.
    /// </summary>
    public IReadOnlyList<string>? ExcludePaths { get; init; }
}
