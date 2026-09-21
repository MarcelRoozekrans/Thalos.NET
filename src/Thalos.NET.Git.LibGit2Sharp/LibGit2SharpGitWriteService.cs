using LibGit2Sharp;
using ZeroAlloc.Results;
using Commands = LibGit2Sharp.Commands;

namespace Thalos.Git.LibGit2Sharp;

/// <summary>
/// <see cref="IGitWriteService"/> over LibGit2Sharp (the native libgit2 backend). LibGit2Sharp's API is synchronous,
/// so every method here runs its work on the thread pool via <see cref="Task.Run{TResult}(Func{TResult}, CancellationToken)"/>
/// rather than block the caller; cancellation is observed before the work starts, not mid-operation (libgit2 calls
/// are not internally cancellable).
/// </summary>
public sealed class LibGit2SharpGitWriteService : IGitWriteService
{
    /// <inheritdoc />
    public ValueTask<Result<GitBranchResult, AgentError>> CreateBranchAsync(string repositoryPath, string branchName, string? sourceRef, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        return RunAsync(() => CreateBranchCore(repositoryPath, branchName, sourceRef), ct);
    }

    /// <inheritdoc />
    public ValueTask<Result<GitCommitResult, AgentError>> CommitAsync(string repositoryPath, string message, GitAuthor? author, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return RunAsync(() => CommitCore(repositoryPath, message, author), ct);
    }

    /// <inheritdoc />
    public ValueTask<Result<GitPushResult, AgentError>> PushAsync(string repositoryPath, string? remoteName, GitCredentials? credentials, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        return RunAsync(() => PushCore(repositoryPath, remoteName, credentials), ct);
    }

    private static Result<GitBranchResult, AgentError> CreateBranchCore(string repositoryPath, string branchName, string? sourceRef)
    {
        if (!Repository.IsValid(repositoryPath))
        {
            return Result<GitBranchResult, AgentError>.Failure(AgentError.GitRepositoryNotFound(repositoryPath));
        }

        try
        {
            using var repo = new Repository(repositoryPath);
            if (repo.Branches[branchName] is not null)
            {
                return Result<GitBranchResult, AgentError>.Failure(AgentError.GitBranchAlreadyExists(branchName));
            }

            Commit target;
            if (sourceRef is null)
            {
                if (repo.Head.Tip is null)
                {
                    return Result<GitBranchResult, AgentError>.Failure(AgentError.Validation("The repository has no commits yet; HEAD has nothing to branch from."));
                }

                target = repo.Head.Tip;
            }
            else
            {
                if (repo.Lookup<Commit>(sourceRef) is not { } resolved)
                {
                    return Result<GitBranchResult, AgentError>.Failure(AgentError.Validation($"'{sourceRef}' does not resolve to a commit."));
                }

                target = resolved;
            }

            var branch = repo.CreateBranch(branchName, target);
            Commands.Checkout(repo, branch);
            return Result<GitBranchResult, AgentError>.Success(new GitBranchResult(branchName, target.Sha));
        }
        catch (LibGit2SharpException ex)
        {
            return Result<GitBranchResult, AgentError>.Failure(AgentError.GitOperationFailed($"Failed to create branch '{branchName}'.", ex.GetType().Name));
        }
    }

    private static Result<GitCommitResult, AgentError> CommitCore(string repositoryPath, string message, GitAuthor? author)
    {
        if (!Repository.IsValid(repositoryPath))
        {
            return Result<GitCommitResult, AgentError>.Failure(AgentError.GitRepositoryNotFound(repositoryPath));
        }

        try
        {
            using var repo = new Repository(repositoryPath);
            Commands.Stage(repo, "*");

            // Compare the index against HEAD's tree (null tree for a repo with no commits yet, which libgit2
            // treats as empty) rather than trusting RetrieveStatus's working-directory view — this is the
            // authoritative "is there anything staged to commit" check.
            var staged = repo.Diff.Compare<TreeChanges>(repo.Head.Tip?.Tree, DiffTargets.Index);
            if (staged.Count == 0)
            {
                return Result<GitCommitResult, AgentError>.Success(new GitCommitResult(repo.Head.Tip?.Sha ?? string.Empty, Created: false));
            }

            Signature? signature;
            try
            {
                // BuildSignature returns null (it does not throw) when user.name/user.email are not configured
                // at any level (Local/Global/Xdg/System) — it never raises the LibGit2SharpException a caller
                // might expect for a missing-config lookup.
                signature = author is null
                    ? repo.Config.BuildSignature(DateTimeOffset.Now)
                    : new Signature(author.Name, author.Email, DateTimeOffset.Now);
            }
            catch (LibGit2SharpException ex)
            {
                return Result<GitCommitResult, AgentError>.Failure(AgentError.Validation($"No commit author was given and none is configured (user.name/user.email): {ex.Message}"));
            }

            if (signature is null)
            {
                return Result<GitCommitResult, AgentError>.Failure(AgentError.Validation("No commit author was given and none is configured (user.name/user.email)."));
            }

            var commit = repo.Commit(message, signature, signature);
            return Result<GitCommitResult, AgentError>.Success(new GitCommitResult(commit.Sha, Created: true));
        }
        catch (LibGit2SharpException ex)
        {
            return Result<GitCommitResult, AgentError>.Failure(AgentError.GitOperationFailed("Failed to commit.", ex.GetType().Name));
        }
    }

    private static Result<GitPushResult, AgentError> PushCore(string repositoryPath, string? remoteName, GitCredentials? credentials)
    {
        if (!Repository.IsValid(repositoryPath))
        {
            return Result<GitPushResult, AgentError>.Failure(AgentError.GitRepositoryNotFound(repositoryPath));
        }

        try
        {
            using var repo = new Repository(repositoryPath);
            var branch = repo.Head;
            if (branch.Tip is null)
            {
                return Result<GitPushResult, AgentError>.Failure(AgentError.Validation("HEAD has no commits to push."));
            }

            var upstreamCreated = false;
            if (branch.TrackedBranch is null)
            {
                var remote = string.IsNullOrWhiteSpace(remoteName) ? "origin" : remoteName;
                if (repo.Network.Remotes[remote] is null)
                {
                    return Result<GitPushResult, AgentError>.Failure(AgentError.GitOperationFailed($"Remote '{remote}' is not configured."));
                }

                repo.Branches.Update(branch, b => b.Remote = remote, b => b.UpstreamBranch = branch.CanonicalName);
                branch = repo.Branches[branch.FriendlyName]; // re-read: pick up the tracking config just written
                upstreamCreated = true;
            }

            var options = new PushOptions();
            if (credentials is not null)
            {
                options.CredentialsProvider = (_, _, _) => new UsernamePasswordCredentials
                {
                    Username = credentials.Username,
                    Password = credentials.Password,
                };
            }

            repo.Network.Push(branch, options);
            return Result<GitPushResult, AgentError>.Success(new GitPushResult(branch.RemoteName ?? "origin", branch.FriendlyName, upstreamCreated));
        }
        catch (LibGit2SharpException ex) when (IsAuthenticationFailure(ex))
        {
            return Result<GitPushResult, AgentError>.Failure(AgentError.GitAuthenticationFailed("Push was rejected: authentication failed.", ex.GetType().Name));
        }
        catch (LibGit2SharpException ex)
        {
            return Result<GitPushResult, AgentError>.Failure(AgentError.GitOperationFailed("Failed to push.", ex.GetType().Name));
        }
    }

    private static bool IsAuthenticationFailure(LibGit2SharpException ex) =>
        ex.Message.Contains("401", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("403", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Authentication", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("credentials", StringComparison.OrdinalIgnoreCase);

    private static async ValueTask<Result<T, AgentError>> RunAsync<T>(Func<Result<T, AgentError>> work, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return await Task.Run(work, ct).ConfigureAwait(false);
    }
}
