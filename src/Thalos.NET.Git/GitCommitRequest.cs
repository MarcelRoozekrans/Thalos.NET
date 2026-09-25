namespace Thalos.Git;

/// <summary>Request to <see cref="Workspaces.IRunWorkspaceGit.CommitAsync"/>.</summary>
public sealed record GitCommitRequest
{
    /// <summary>The commit message.</summary>
    public required string Message { get; init; }

    /// <summary>
    /// The commit's author and committer identity — the same identity is recorded as both, as
    /// <see cref="GitAuthor"/> already documents. <see langword="null"/> uses a fixed fallback identity: a
    /// supported configuration for a caller with no configured identity, such as a test (ruling R27). A production
    /// caller is expected to always supply its own configured commit author, since <see cref="Workspaces.GitCli"/>'s
    /// host-configuration isolation means no <c>user.name</c>/<c>user.email</c> can ever be picked up from git
    /// config instead.
    /// </summary>
    public GitAuthor? Author { get; init; }

    /// <summary>Repository-relative paths, confined to the workspace root, to stage. <see langword="null"/> stages every change in the worktree.</summary>
    public IReadOnlyList<string>? Paths { get; init; }

    /// <summary>Repository-relative paths, confined to the workspace root, never staged by this commit — unstaged after <see cref="Paths"/> is applied.</summary>
    public IReadOnlyList<string>? ExcludePaths { get; init; }
}
