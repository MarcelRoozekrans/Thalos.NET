namespace Thalos.Git;

/// <summary>Result of <see cref="IPullRequestPublisher.OpenPullRequestAsync"/>.</summary>
/// <param name="Url">
/// The pull request's web URL. Always populated — every hosting platform gives a pull request a URL, even ones
/// (Azure DevOps) whose primary identifier is a number rather than a slug.
/// </param>
/// <param name="Id">
/// The platform's own identifier for the pull request (e.g. a GitHub pull request number, an Azure DevOps pull
/// request id), as a string so callers do not need to know each platform's underlying numeric type.
/// <see langword="null"/> when a platform exposes no identifier distinct from <paramref name="Url"/>.
/// </param>
public sealed record PullRequestResult(string Url, string? Id);
