using ZeroAlloc.Results;

namespace Thalos.Git;

/// <summary>
/// Opens a pull request proposing to merge one branch into another on whatever hosting platform the repository at
/// <c>repositoryPath</c> is pushed to. This interface says only what every pull request needs — a source branch, a
/// target branch, a title, a body — and returns something a caller can point a person at. It knows nothing about
/// GitHub, Azure DevOps, or any other forge, and nothing in this package (or anywhere in Thalos.NET) implements it:
/// the consuming host resolves which platform a given repository lives on and implements this contract against that
/// platform's own API.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="IGitWriteService"/>, not a fourth method on it (see that interface's own
/// remarks, which call pull-request publishing out by name as excluded). <see cref="IGitWriteService"/> is a purely
/// local operation — it opens the repository on disk and never makes a network call except the push transport
/// itself. Publishing a pull request is a call to a remote, host-specific API with its own authentication, rate
/// limits, and failure modes. A caller that only wants local git write (branch, commit, push) should not have to
/// also depend on a hosting abstraction — real or stubbed — to get it.
/// </remarks>
public interface IPullRequestPublisher
{
    /// <summary>
    /// Opens a pull request proposing to merge <paramref name="sourceBranch"/> into <paramref name="targetBranch"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="sourceBranch"/> is expected to already exist on the remote (see
    /// <see cref="IGitWriteService.PushAsync"/>) — a branch that exists only in a local working tree cannot be
    /// proposed for merge on a hosting platform that has never seen it. This contract does not specify what happens
    /// for a missing source branch, an unreachable host, or an already-open pull request for the same branch pair;
    /// each implementation defines its own failure semantics for those and reports them through the returned
    /// <see cref="AgentError"/>.
    /// </remarks>
    /// <param name="repositoryPath">
    /// Path to the working tree root (the directory containing <c>.git</c>). Implementations use this to resolve
    /// which hosted repository — platform, owner, name — the pull request is opened against, typically by reading
    /// the remote's URL.
    /// </param>
    /// <param name="sourceBranch">The branch containing the changes to merge.</param>
    /// <param name="targetBranch">The branch the changes should be merged into.</param>
    /// <param name="title">The pull request title.</param>
    /// <param name="body">The pull request description.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<Result<PullRequestResult, AgentError>> OpenPullRequestAsync(string repositoryPath, string sourceBranch, string targetBranch, string title, string body, CancellationToken ct);
}
