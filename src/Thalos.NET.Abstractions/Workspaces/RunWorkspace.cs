namespace Thalos.Workspaces;

/// <summary>
/// One run's private git worktree: a directory an agent writes into through the <c>workspace__*</c> tools, and the
/// git identity that describes where its changes go. <see cref="Root"/> is a security boundary — every path an
/// agent supplies must be confined to it through <see cref="WorkspacePath.Resolve"/> before it touches the
/// filesystem.
/// </summary>
/// <param name="RunId">The run the workspace belongs to.</param>
/// <param name="Repository">The repository the run works against, as a local path or clone URL.</param>
/// <param name="Remote">
/// The URL of the git remote to fetch from and push to. Every run against the same repository shares one mirror, and
/// a new workspace re-targets that mirror's <c>origin</c> at its own remote. Commits and pushes refuse unless the
/// mirror's <c>origin</c> still holds exactly this URL, so re-targeting a repository at a different remote makes
/// every older run's commits and pushes fail closed.
/// </param>
/// <param name="DefaultBranch">
/// The repository's default branch, e.g. <c>"main"</c>. <see cref="BaseRef"/> is the literal string
/// <c>"origin/" + DefaultBranch</c>; it always assumes the remote is configured under the name <c>"origin"</c> and
/// does not read <see cref="Remote"/>'s URL to determine that.
/// </param>
/// <param name="Branch">The branch the workspace checks out and commits to.</param>
/// <param name="Root">The worktree's root directory on disk.</param>
/// <param name="SolutionPath">
/// Path to the solution file to build: absolute, canonical and confined to <see cref="Root"/> — the value
/// <see cref="WorkspacePath.Resolve"/> returns, not a path relative to <see cref="Root"/>. <see langword="null"/>
/// when the repository has no solution file, or the run does not need one.
/// </param>
public sealed record RunWorkspace(Guid RunId, string Repository, string Remote, string DefaultBranch, string Branch, string Root, string? SolutionPath)
{
    /// <summary>The ref the branch was cut from, e.g. "origin/main" — what a diff for the run is taken against.</summary>
    public string BaseRef => $"origin/{DefaultBranch}";

    /// <summary>
    /// When the workspace became ready, from the provider's <c>TimeProvider</c>; persisted in the sidecar record. The
    /// sweeper removes a workspace with no run row only once it is older than a grace period, because a run's
    /// workspace exists before its run row does. Stamped as the ready record is published, after the clone or fetch
    /// and the worktree checkout, so a slow create does not use up the grace; the ready observers run after it, so
    /// the time they take still counts against the grace. A provider may stamp a provisional record too, but its
    /// liveness is the provider's business, not this value's.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }
}
