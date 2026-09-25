namespace Thalos.Workspaces;

/// <summary>
/// One run's private git worktree: a directory an agent writes into through the <c>workspace__*</c> tools, and the
/// git identity that describes where its changes go. <see cref="Root"/> is a security boundary — every path an
/// agent supplies must be confined to it through <see cref="WorkspacePath.Resolve"/> before it touches the
/// filesystem.
/// </summary>
/// <param name="RunId">The run the workspace belongs to.</param>
/// <param name="Repository">The repository the run works against, as a local path or clone URL.</param>
/// <param name="Remote">The URL of the git remote to fetch from and push to.</param>
/// <param name="DefaultBranch">
/// The repository's default branch, e.g. <c>"main"</c>. <see cref="BaseRef"/> is the literal string
/// <c>"origin/" + DefaultBranch</c> — it is not built from <see cref="Remote"/>, which may itself be a different
/// remote name or URL.
/// </param>
/// <param name="Branch">The branch the workspace checks out and commits to.</param>
/// <param name="Root">The worktree's root directory on disk.</param>
/// <param name="SolutionPath">
/// Path to the solution file to build, relative to <see cref="Root"/>. <see langword="null"/> when the repository
/// has no solution file, or the run does not need one.
/// </param>
public sealed record RunWorkspace(Guid RunId, string Repository, string Remote, string DefaultBranch, string Branch, string Root, string? SolutionPath)
{
    /// <summary>The ref the branch was cut from, e.g. "origin/main" — what a diff for the run is taken against.</summary>
    public string BaseRef => $"origin/{DefaultBranch}";

    /// <summary>
    /// When the provider created the workspace, from its <c>TimeProvider</c>; persisted in the sidecar record. The
    /// sweeper removes a workspace with no run row only once it is older than a grace period, because a run's
    /// workspace exists before its run row does.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }
}
