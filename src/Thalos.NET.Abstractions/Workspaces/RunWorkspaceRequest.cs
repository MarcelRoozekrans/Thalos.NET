namespace Thalos.Workspaces;

/// <summary>
/// What a run's workspace should be created from. Host code builds this from the run row before the workspace
/// exists — see <see cref="IRunWorkspaceProvider.CreateAsync"/>.
/// </summary>
/// <param name="RunId">The run the workspace belongs to.</param>
/// <param name="Repository">The repository the run works against, as a local path or clone URL.</param>
/// <param name="Remote">The URL of the git remote to fetch from and push to.</param>
/// <param name="DefaultBranch">
/// The repository's default branch, e.g. <c>"main"</c>. <see cref="RunWorkspace.BaseRef"/> is the literal string
/// <c>"origin/" + DefaultBranch</c> — it is not built from <see cref="Remote"/>, which may itself be a different
/// remote name or URL.
/// </param>
/// <param name="Branch">The branch the workspace checks out and commits to.</param>
/// <param name="Solution">
/// Path to the solution file to build, relative to the workspace root. <see langword="null"/> when the repository
/// has no solution file, or the run does not need one.
/// </param>
public sealed record RunWorkspaceRequest(Guid RunId, string Repository, string Remote, string DefaultBranch, string Branch, string? Solution);
