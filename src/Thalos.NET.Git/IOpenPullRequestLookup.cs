using ZeroAlloc.Results;

namespace Thalos.Git;

/// <summary>
/// Looks up whether a pull request is already open for a given source branch, on whatever hosting platform the
/// repository is pushed to.
/// </summary>
/// <remarks>
/// Deliberately its own interface, separate from <see cref="IPullRequestPublisher"/> (ruling R28a, amending R28).
/// <c>GitActionTools</c>'s own doc comment states it depends only on write-only surfaces, and
/// <c>GitActionToolsDependencySurfaceTests</c> enforces that mechanically by denylisting any read-shaped method
/// name (<c>Find</c> among them) on either of <c>GitActionTools</c>'s two allowed dependencies. Adding this lookup
/// directly to <see cref="IPullRequestPublisher"/> — one of those two dependencies — would have handed
/// <c>GitActionTools</c> a read surface through a dependency it already legitimately holds, which is exactly what
/// that guard exists to prevent; exempting the method by name would have weakened the guard instead of respecting
/// it. This interface is additive and abstract by construction, so introducing it is not a breaking change; nothing
/// depends on it yet, and <c>GitActionTools</c> never will.
/// </remarks>
public interface IOpenPullRequestLookup
{
    /// <summary>
    /// The open pull request whose source branch is <paramref name="sourceBranch"/> on the repository hosted at
    /// <paramref name="remoteUrl"/>, or <see langword="null"/> when none is open.
    /// </summary>
    /// <remarks>
    /// Takes <paramref name="remoteUrl"/>, not a working-tree path, so an implementation never has to open a
    /// repository on disk to find out which hosted repository — platform, owner, name — to query (ruling R22).
    /// </remarks>
    /// <param name="remoteUrl">The git remote's URL, e.g. <see cref="Thalos.Workspaces.RunWorkspace.Remote"/>.</param>
    /// <param name="sourceBranch">The branch to look for an open pull request from.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<Result<PullRequestResult?, AgentError>> FindOpenPullRequestAsync(string remoteUrl, string sourceBranch, CancellationToken ct);
}
