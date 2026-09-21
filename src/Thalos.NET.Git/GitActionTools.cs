using System.ComponentModel;

namespace Thalos.Git;

/// <summary>
/// Git write actions exposed to Thalos agents: <c>git__create_branch</c>, <c>git__commit</c>, <c>git__push</c> and
/// <c>git__open_pull_request</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only <see cref="IGitWriteService"/> and <see cref="IPullRequestPublisher"/>.</b> Both are write-only — neither
/// exposes a way to read a repository, inspect a diff, walk commit history, or read back a pull request's contents
/// — so this class has no path to a read surface however it is called. That mirrors the rule Daedalus's own
/// repository-action tool class applies to its one GitHub write dependency: its doc comment says pairing a reader
/// and a writer in one class would quietly defeat the boundary the split exists to establish. Here that means two
/// dependencies rather than one, not because the rule is different, but because these two are genuinely different
/// concerns — a local, on-disk git operation and a call to a remote hosting platform — rather than a read half and
/// a write half of the same one; see <see cref="IPullRequestPublisher"/>'s own remarks for why they are not merged
/// into a single interface.
/// </para>
/// <para>
/// <b>Ordering.</b> These four tools have a real dependency order, and each tool's description says so rather than
/// leaving an agent to discover it by failing: create a branch, commit onto it, push it, then open a pull request
/// from it. A commit with nothing pushed after it has changed nothing anyone else can see; a pull request opened
/// from a branch nobody has pushed has nothing on the remote to point at.
/// </para>
/// </remarks>
/// <param name="gitWriter">Local git write operations: create a branch, commit, push.</param>
/// <param name="pullRequestPublisher">Opens a pull request on whatever hosting platform the repository is pushed to.</param>
[ThalosToolType]
public sealed class GitActionTools(IGitWriteService gitWriter, IPullRequestPublisher pullRequestPublisher)
{
    /// <summary><c>git__create_branch</c>: creates and checks out a new local branch.</summary>
    [ThalosTool("create_branch")]
    [Description(
        "Create a new local branch and check it out. This is normally the first of four ordered steps for " +
        "proposing a change: create_branch, then commit, then push, then open_pull_request. Fails if the branch " +
        "already exists; it never switches to the existing branch instead.")]
    public async Task<string> CreateBranch(
        [Description("Path to the working tree root (the directory containing .git).")] string repositoryPath,
        [Description("The branch to create. Must not already exist locally.")] string branchName,
        [Description("Branch, tag, or commit sha to branch from. Omit to branch from the current HEAD.")] string? sourceRef = null,
        CancellationToken ct = default)
    {
        var result = await gitWriter.CreateBranchAsync(repositoryPath, branchName, sourceRef, ct).ConfigureAwait(false);
        return result.IsSuccess
            ? $"Created and checked out branch '{result.Value.BranchName}' at {result.Value.Sha}."
            : $"Could not create the branch: {result.Error}";
    }

    /// <summary><c>git__commit</c>: stages every pending change and commits it on the current branch.</summary>
    [ThalosTool("commit")]
    [Description(
        "Stage every pending change (new, modified and deleted files) and commit it on the current branch of the " +
        "working tree. A commit is local only: nothing anyone else can see changes until you also call git__push " +
        "on the same working tree. Succeeds without creating a commit when there is nothing to commit.")]
    public async Task<string> Commit(
        [Description("Path to the working tree root (the directory containing .git).")] string repositoryPath,
        [Description("The commit message.")] string message,
        [Description("Author display name. Supply together with authorEmail, or omit both to use the repository's configured git identity.")] string? authorName = null,
        [Description("Author email address. Supply together with authorName, or omit both to use the repository's configured git identity.")] string? authorEmail = null,
        CancellationToken ct = default)
    {
        if (authorName is null != authorEmail is null)
        {
            return "Could not commit: authorName and authorEmail must be supplied together, or both omitted.";
        }

        var author = authorName is not null && authorEmail is not null ? new GitAuthor(authorName, authorEmail) : null;
        var result = await gitWriter.CommitAsync(repositoryPath, message, author, ct).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return $"Could not commit: {result.Error}";
        }

        return result.Value.Created
            ? $"Committed {result.Value.Sha}. Nothing anyone else can see until you call git__push."
            : "Nothing to commit — the working tree already matches the last commit.";
    }

    /// <summary><c>git__push</c>: pushes the current branch to a remote, creating the upstream if none exists yet.</summary>
    [ThalosTool("push")]
    [Description(
        "Push the current branch to a remote so its commits become visible to anyone with access to the " +
        "repository. Call this after git__commit — a commit that is never pushed stays invisible to everyone " +
        "else, and git__open_pull_request needs the branch to already exist on the remote. Creates the upstream " +
        "tracking branch automatically the first time a new branch is pushed.")]
    public async Task<string> Push(
        [Description("Path to the working tree root (the directory containing .git).")] string repositoryPath,
        [Description("The remote to push to when the branch has no upstream yet. Omit for 'origin'. Ignored once an upstream is already configured.")] string? remoteName = null,
        [Description("Username for an HTTP(S) remote (e.g. a fixed placeholder such as x-access-token). Omit for SSH or local remotes.")] string? username = null,
        [Description("Password or personal access token for an HTTP(S) remote. Omit for SSH or local remotes.")] string? password = null,
        CancellationToken ct = default)
    {
        if (username is null != password is null)
        {
            return "Could not push: username and password must be supplied together, or both omitted.";
        }

        var credentials = username is not null && password is not null ? new GitCredentials(username, password) : null;
        var result = await gitWriter.PushAsync(repositoryPath, remoteName, credentials, ct).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return $"Could not push: {result.Error}";
        }

        return result.Value.UpstreamCreated
            ? $"Pushed and created the upstream '{result.Value.RemoteName}/{result.Value.BranchName}'."
            : $"Pushed to '{result.Value.RemoteName}/{result.Value.BranchName}'.";
    }

    /// <summary><c>git__open_pull_request</c>: opens a pull request proposing to merge one pushed branch into another.</summary>
    [ThalosTool("open_pull_request")]
    [Description(
        "Open a pull request proposing to merge one branch into another. The source branch must already have " +
        "been pushed with git__push — a branch that only exists locally cannot be proposed for merge on a " +
        "hosting platform that has never seen it. This is normally the last of four ordered steps: create_branch, " +
        "commit, push, then open_pull_request.")]
    public async Task<string> OpenPullRequest(
        [Description("Path to the working tree root (the directory containing .git); used to resolve which hosted repository to open the pull request against.")] string repositoryPath,
        [Description("The branch containing the changes, already pushed to the remote.")] string sourceBranch,
        [Description("The branch the changes should be merged into.")] string targetBranch,
        [Description("The pull request title.")] string title,
        [Description("The pull request description.")] string body,
        CancellationToken ct = default)
    {
        var result = await pullRequestPublisher.OpenPullRequestAsync(repositoryPath, sourceBranch, targetBranch, title, body, ct).ConfigureAwait(false);
        return result.IsSuccess
            ? $"Opened pull request: {result.Value.Url}"
            : $"Could not open the pull request: {result.Error}";
    }
}
