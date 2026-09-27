using Thalos.Workspaces;

namespace Thalos.Git.Workspaces;

/// <summary>
/// Supplies HTTP(S) credentials for a git remote by URL, so <see cref="GitWorktreeWorkspaceProvider"/> can clone and
/// fetch a private mirror without the credentials ever being written to git config (see
/// <see cref="GitWorktreeWorkspaceProvider"/>'s remarks).
/// </summary>
public interface IGitCredentialSource
{
    /// <summary>
    /// The credentials to send to <paramref name="remoteUrl"/>, or <see langword="null"/> for anonymous git — a
    /// supported configuration for a public or local remote (ruling R27), and also what a run whose repository has
    /// no <see cref="IGitCredentialSource"/> registered at all gets, since <see cref="RunWorkspaceRequest.Remote"/>
    /// is fetched anonymously whenever no source is configured or the configured one returns <see langword="null"/>
    /// for this particular remote.
    /// </summary>
    /// <param name="remoteUrl">The remote's URL, from <see cref="RunWorkspaceRequest.Remote"/>.</param>
    GitCredentials? GetCredentials(string remoteUrl);
}
