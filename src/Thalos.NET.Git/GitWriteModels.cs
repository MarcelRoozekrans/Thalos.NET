namespace Thalos.Git;

/// <summary>Result of <see cref="IGitWriteService.CreateBranchAsync"/>.</summary>
/// <param name="BranchName">The branch created and checked out.</param>
/// <param name="Sha">The commit sha the branch points at immediately after creation (the resolved <c>sourceRef</c>).</param>
public sealed record GitBranchResult(string BranchName, string Sha);

/// <summary>Result of <see cref="IGitWriteService.CommitAsync"/>.</summary>
/// <param name="Sha">HEAD's sha after the call: the new commit's sha when <paramref name="Created"/> is true, otherwise unchanged.</param>
/// <param name="Created">
/// <see langword="false"/> when there was nothing to commit (the working tree matched HEAD after staging), in which
/// case no commit was created; <see langword="true"/> when a new commit was made.
/// </param>
public sealed record GitCommitResult(string Sha, bool Created);

/// <summary>Result of <see cref="IGitWriteService.PushAsync"/>.</summary>
/// <param name="RemoteName">The remote pushed to.</param>
/// <param name="BranchName">The branch pushed.</param>
/// <param name="UpstreamCreated">
/// <see langword="true"/> when this call established the upstream tracking branch (there was none before);
/// <see langword="false"/> when an existing upstream was pushed to.
/// </param>
public sealed record GitPushResult(string RemoteName, string BranchName, bool UpstreamCreated);

/// <summary>
/// A commit author/committer identity for <see cref="IGitWriteService.CommitAsync"/>. The same identity is recorded
/// as both author and committer — this contract does not distinguish them.
/// </summary>
/// <param name="Name">The display name recorded on the commit.</param>
/// <param name="Email">The email address recorded on the commit.</param>
public sealed record GitAuthor(string Name, string Email);

/// <summary>
/// Username/password (or personal-access-token-as-password) credentials for <see cref="IGitWriteService.PushAsync"/>
/// against an HTTP(S) remote. Omit for SSH remotes (the system's SSH agent/config is used) or local file remotes.
/// </summary>
/// <param name="Username">The username, or a fixed placeholder such as <c>"x-access-token"</c> when the host authenticates by token alone.</param>
/// <param name="Password">The password or personal access token.</param>
public sealed record GitCredentials(string Username, string Password);
