using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Git.Workspaces;

/// <summary>
/// <see cref="IRunWorkspaceGit"/> over a run's own worktree, driven entirely through <see cref="GitCli"/> — a real
/// <c>git</c> child process awaited with <see cref="System.Diagnostics.Process.WaitForExitAsync(CancellationToken)"/>,
/// never LibGit2Sharp and never <see cref="Task.Run{TResult}(Func{TResult})"/> to fake async over a synchronous API
/// (ruling R23). Every call therefore also gets <see cref="GitCli"/>'s own isolation from host and repository git
/// configuration — <c>GIT_CONFIG_NOSYSTEM</c>, an empty <c>GIT_CONFIG_GLOBAL</c>, an isolated <c>HOME</c>/
/// <c>XDG_CONFIG_HOME</c>, and <c>-c core.hooksPath=&lt;an empty provider-owned directory&gt;</c> — so a host or
/// repository hook (<c>pre-commit</c>, <c>commit-msg</c>, <c>post-commit</c>, ...) never runs on a commit this type
/// makes, and <c>-c core.fsmonitor=false</c>, so an fsmonitor hook a repository's config names never runs either;
/// see <see cref="GitCli"/>'s own remarks for the full mechanism.
/// </summary>
/// <remarks>
/// <para>
/// <b>Path-scoped commit, and no leftover from a failed attempt.</b> <see cref="CommitAsync"/> first resets the
/// index to <c>HEAD</c> with a bare <c>git reset -q</c> — a failed commit attempt earlier in the same worktree (a
/// blank message, say) can leave files staged from that attempt, and this call must always start from
/// <c>HEAD</c>'s own state, never a previous call's leftovers. It then stages
/// <see cref="GitCommitRequest.Paths"/> with <c>git add -A -- &lt;Paths&gt;</c> (or <c>git add -A</c> when
/// <see cref="GitCommitRequest.Paths"/> is <see langword="null"/>), then unstages
/// <see cref="GitCommitRequest.ExcludePaths"/> with <c>git reset -q -- &lt;ExcludePaths&gt;</c>. Every one of these
/// three calls passes <c>--literal-pathspecs</c>, a global git flag stated as an argument (never an environment
/// variable — <see cref="GitCli"/> strips every inherited <c>GIT_*</c> variable, including
/// <c>GIT_LITERAL_PATHSPECS</c>, on every call), so a path such as <c>"*"</c> or <c>"AGENT.m?"</c> is matched
/// literally by its exact name rather than as a glob — a caller-supplied path is data, never a pattern. The commit
/// itself is then a plain <c>git commit -m &lt;Message&gt;</c> with no path list of its own: by the time it runs,
/// the index already holds exactly the paths this call means to commit, so a second, redundant path restriction on
/// <c>commit</c> (either <c>git commit -- &lt;paths&gt;</c> or <c>git commit --only -- &lt;paths&gt;</c>) would
/// either duplicate what <c>add</c>/<c>reset</c> already did or, for <c>--only</c>, actively conflict with a prior
/// <c>reset</c> of paths outside <see cref="GitCommitRequest.Paths"/> that this call never touched. Every path is
/// validated with <see cref="WorkspacePath.Resolve"/> against the worktree root before any git command runs, and
/// passed to git exactly as given — relative to <see cref="RunWorkspace.Root"/>, which is also
/// <see cref="GitCli.RunAsync"/>'s working directory — through <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>,
/// with <c>--</c> ahead of every path list, so nothing in a path is ever interpreted by a shell or mistaken for a
/// git option.
/// </para>
/// <para>
/// <b>Commit identity.</b> <see cref="GitCommitRequest.Author"/> — required (ruling R27) — is passed as
/// <c>-c user.name=</c>/<c>-c user.email=</c> — never read from git config, which <see cref="GitCli"/> isolates
/// from the host entirely, and never left to a repository config that isolation does not reach (a mirror's own
/// <c>.git/config</c>). <c>-c commit.gpgsign=false</c> is always passed too, forcing signing off explicitly rather
/// than only relying on isolation hiding a host or repository <c>commit.gpgsign=true</c> — defense in depth, the
/// same reasoning <see cref="GitCli"/> itself gives for stating its hooksPath and protocol flags explicitly on top
/// of removing the config layers that would otherwise carry them.
/// </para>
/// <para>
/// <b>Push trusts nothing but the workspace record, writes no shared config, and needs no mirror lock.</b>
/// <see cref="PushAsync"/> refuses outright when <see cref="RunWorkspace.Branch"/> equals
/// <see cref="RunWorkspace.DefaultBranch"/> — a run's push must never be able to fast-forward the repository's own
/// default branch — and otherwise verifies with <c>git symbolic-ref -q HEAD</c> that the worktree's checked-out
/// branch really is <c>refs/heads/&lt;Branch&gt;</c> before pushing anything, so a workspace record that has been
/// tampered with, or a caller that passes a workspace for the wrong worktree, is refused rather than trusted. The
/// push itself names the explicit two-sided refspec <c>refs/heads/&lt;Branch&gt;:refs/heads/&lt;Branch&gt;</c> —
/// never a bare <c>HEAD:...</c>, which would push whatever branch the worktree happens to be on regardless of what
/// the refspec's destination says — and goes straight to <see cref="RunWorkspace.Remote"/> as the push's own
/// repository argument (with <c>--end-of-options</c> ahead of it, so a remote or branch name that happens to start
/// with <c>-</c> can never be parsed as a flag), rather than through a named <c>origin</c> remote whose URL this
/// call would otherwise have to write into the mirror's shared, cross-run config first. Earlier revisions wrote
/// <c>remote.origin.url</c> into that shared config before every push — a write with no mirror lock around it, racing
/// every other run sharing the same mirror, and not atomic with the push that followed it. Pushing directly to the
/// URL removes that shared, unlocked write entirely: nothing this call does is visible to any other run.
/// </para>
/// <para>
/// <b>Credentials never reach argv or disk.</b> <see cref="PushAsync"/> asks <c>credentials</c> for
/// <see cref="RunWorkspace.Remote"/> exactly as <see cref="GitWorktreeWorkspaceProvider"/> does, and passes a
/// resulting <c>AUTHORIZATION</c> header through <see cref="GitCli.RunAsync"/>'s <c>secretConfig</c> — the
/// <c>GIT_CONFIG_COUNT</c>/<c>GIT_CONFIG_KEY_n</c>/<c>GIT_CONFIG_VALUE_n</c> environment mechanism — never as a
/// <c>-c</c> argument and never written to git config. A failure message is scrubbed of the credential value first,
/// the same as <see cref="GitWorktreeWorkspaceProvider"/>.
/// </para>
/// </remarks>
/// <param name="options">Where the git child process runs from and how it is bounded; shared with <see cref="GitWorktreeWorkspaceProvider"/>.</param>
/// <param name="logger">Required: every host has one.</param>
/// <param name="credentials">
/// Supplies HTTP(S) credentials per remote for <see cref="PushAsync"/>. <see langword="null"/> means every push is
/// anonymous — a supported configuration for a public or local remote (ruling R27), and the push then carries no
/// authorization header at all.
/// </param>
public sealed partial class GitCliRunWorkspaceGit(
    GitWorkspaceOptions options, ILogger<GitCliRunWorkspaceGit> logger, IGitCredentialSource? credentials = null) : IRunWorkspaceGit
{
    private readonly GitCli _git = new(options);

    /// <inheritdoc />
    public async ValueTask<Result<GitCommitResult, AgentError>> CommitAsync(RunWorkspace workspace, GitCommitRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(request);

        // Checked before any staging, not only at create (GitWorktreeWorkspaceProvider's own mirror validation):
        // the mirror's config, or this worktree's own config.worktree, could have been changed after a successful
        // create but before this commit — a clean or smudge filter driver planted in the interval runs arbitrary
        // commands on add, reset and commit otherwise (fix round 3 and 4 rulings). Run from the worktree, so git
        // reports every key it will use here, config.worktree included.
        var violation = await MirrorConfigSurface.FindViolationAsync(_git, workspace.Root, workspace.Remote, ct).ConfigureAwait(false);
        if (violation is not null)
        {
            return Result<GitCommitResult, AgentError>.Failure(AgentError.Validation(
                $"Refusing to commit: the git config this worktree uses is outside the allowed surface. Detail: {violation}"));
        }

        if (ValidatePaths(workspace.Root, request) is { } invalidPath)
        {
            return Result<GitCommitResult, AgentError>.Failure(invalidPath);
        }

        var staged = await StageAsync(workspace.Root, request, ct).ConfigureAwait(false);
        if (staged is { } stageFailure)
        {
            return Result<GitCommitResult, AgentError>.Failure(stageFailure);
        }

        var cached = await _git.RunAsync(workspace.Root, ["diff", "--cached", "--quiet"], null, null, ct).ConfigureAwait(false);
        if (cached.TimedOut)
        {
            return Result<GitCommitResult, AgentError>.Failure(AgentError.GitOperationFailed("git diff --cached failed. The git command timed out."));
        }

        if (cached.ExitCode == 0)
        {
            // Nothing staged: git diff --cached --quiet's own convention for "no differences". No commit is made,
            // and Sha reports HEAD unchanged, per GitCommitResult's existing shape.
            var unchanged = await _git.RunAsync(workspace.Root, ["rev-parse", "HEAD"], null, null, ct).ConfigureAwait(false);
            return unchanged.Succeeded
                ? Result<GitCommitResult, AgentError>.Success(new GitCommitResult(unchanged.StdOut.Trim(), Created: false))
                : Result<GitCommitResult, AgentError>.Failure(GitFailure("git rev-parse HEAD failed.", unchanged, secret: null));
        }

        if (cached.ExitCode != 1)
        {
            return Result<GitCommitResult, AgentError>.Failure(GitFailure("git diff --cached failed.", cached, secret: null));
        }

        var commitConfig = new[] { $"user.name={request.Author.Name}", $"user.email={request.Author.Email}", "commit.gpgsign=false" };
        var committed = await _git.RunAsync(workspace.Root, ["commit", "-m", request.Message], commitConfig, null, ct).ConfigureAwait(false);
        if (!committed.Succeeded)
        {
            return Result<GitCommitResult, AgentError>.Failure(GitFailure("git commit failed.", committed, secret: null));
        }

        var head = await _git.RunAsync(workspace.Root, ["rev-parse", "HEAD"], null, null, ct).ConfigureAwait(false);
        return head.Succeeded
            ? Result<GitCommitResult, AgentError>.Success(new GitCommitResult(head.StdOut.Trim(), Created: true))
            : Result<GitCommitResult, AgentError>.Failure(GitFailure("git rev-parse HEAD failed.", head, secret: null));
    }

    /// <summary>
    /// Resets the index to <c>HEAD</c> (clearing any leftover from a previous, failed commit attempt in this same
    /// worktree), stages <see cref="GitCommitRequest.Paths"/> (or everything), and then unstages
    /// <see cref="GitCommitRequest.ExcludePaths"/> — in that order, so an excluded path staged by a broad
    /// <c>add -A</c> is always unstaged again afterwards. Returns the failure, or <see langword="null"/> on success.
    /// </summary>
    private async Task<AgentError?> StageAsync(string root, GitCommitRequest request, CancellationToken ct)
    {
        var resetToHead = await _git.RunAsync(root, ["--literal-pathspecs", "reset", "-q"], null, null, ct).ConfigureAwait(false);
        if (!resetToHead.Succeeded)
        {
            return GitFailure("git reset failed.", resetToHead, secret: null);
        }

        var addArgs = new List<string> { "--literal-pathspecs", "add", "-A" };
        if (request.Paths is { Count: > 0 } paths)
        {
            addArgs.Add("--");
            addArgs.AddRange(paths);
        }

        var added = await _git.RunAsync(root, addArgs, null, null, ct).ConfigureAwait(false);
        if (!added.Succeeded)
        {
            return GitFailure("git add failed.", added, secret: null);
        }

        if (request.ExcludePaths is not { Count: > 0 } excludePaths)
        {
            return null;
        }

        var resetArgs = new List<string> { "--literal-pathspecs", "reset", "-q", "--" };
        resetArgs.AddRange(excludePaths);
        var reset = await _git.RunAsync(root, resetArgs, null, null, ct).ConfigureAwait(false);
        return reset.Succeeded ? null : GitFailure("git reset failed.", reset, secret: null);
    }

    /// <summary>
    /// Refuses any <see cref="GitCommitRequest.Paths"/> or <see cref="GitCommitRequest.ExcludePaths"/> entry that
    /// <see cref="WorkspacePath.Resolve"/> refuses, before any git command runs.
    /// </summary>
    private static AgentError? ValidatePaths(string root, GitCommitRequest request) =>
        InvalidPath(root, request.Paths) ?? InvalidPath(root, request.ExcludePaths);

    private static AgentError? InvalidPath(string root, IReadOnlyList<string>? paths)
    {
        if (paths is null)
        {
            return null;
        }

        foreach (var path in paths)
        {
            var resolved = WorkspacePath.Resolve(root, path);
            if (resolved.IsFailure)
            {
                return resolved.Error;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<GitFileChange>, AgentError>> DiffStatAsync(RunWorkspace workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var mergeBase = await _git.RunAsync(workspace.Root, ["merge-base", workspace.BaseRef, "HEAD"], null, null, ct).ConfigureAwait(false);
        if (!mergeBase.Succeeded)
        {
            return Result<IReadOnlyList<GitFileChange>, AgentError>.Failure(GitFailure("git merge-base failed.", mergeBase, secret: null));
        }

        // --no-renames: a renamed file is reported as a delete of the old path and an add of the new one, so a
        // caller never has to special-case a rename's own numstat shape. -z: NUL-terminated records with raw,
        // unquoted paths, so a filename containing a newline, a tab, or non-ASCII bytes parses correctly instead of
        // being C-style quoted (the default, newline-delimited format) or split apart by an embedded newline.
        var diff = await _git.RunAsync(workspace.Root, ["diff", "--no-renames", "-z", "--numstat", mergeBase.StdOut.Trim(), "HEAD"], null, null, ct).ConfigureAwait(false);
        return diff.Succeeded
            ? Result<IReadOnlyList<GitFileChange>, AgentError>.Success(ParseNumstat(diff.StdOut))
            : Result<IReadOnlyList<GitFileChange>, AgentError>.Failure(GitFailure("git diff --numstat failed.", diff, secret: null));
    }

    /// <summary>
    /// Parses <c>git diff --no-renames -z --numstat</c> output: one NUL-terminated <c>added\tdeleted\tpath</c>
    /// record per changed file. A binary file's counts are the literal text <c>-</c>, read as <c>0</c>, per this
    /// method's own contract. The path is everything after the second tab, taken as a whole even if it itself
    /// contains a tab.
    /// </summary>
    private static List<GitFileChange> ParseNumstat(string stdOut)
    {
        var changes = new List<GitFileChange>();
        foreach (var record in stdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = record.Split('\t', 3);
            if (parts.Length < 3)
            {
                continue;
            }

            changes.Add(new GitFileChange(parts[2], ParseNumstatCount(parts[0]), ParseNumstatCount(parts[1])));
        }

        return changes;
    }

    private static int ParseNumstatCount(string field) =>
        string.Equals(field, "-", StringComparison.Ordinal) ? 0 : int.Parse(field, NumberStyles.None, CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public async ValueTask<UnitResult<AgentError>> PushAsync(RunWorkspace workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        // Checked here too, not only at create (GitWorktreeWorkspaceProvider's own mirror validation): the
        // mirror's config could have been tampered with after a successful create but before this push — a
        // url.<x>.pushInsteadOf planted in the interval would otherwise redirect this exact push, credentials
        // included, before any network call happens (fix round 2 ruling).
        var violation = await MirrorConfigSurface.FindViolationAsync(_git, workspace.Root, workspace.Remote, ct).ConfigureAwait(false);
        if (violation is not null)
        {
            return UnitResult<AgentError>.Failure(AgentError.Validation(
                $"Refusing to push: the git config this worktree uses is outside the allowed surface. Detail: {violation}"));
        }

        if (string.Equals(workspace.Branch, workspace.DefaultBranch, StringComparison.Ordinal))
        {
            return UnitResult<AgentError>.Failure(AgentError.Validation(
                $"Refusing to push: the run's branch '{workspace.Branch}' is the repository's own default branch."));
        }

        var expectedRef = $"refs/heads/{workspace.Branch}";
        var symbolicRef = await _git.RunAsync(workspace.Root, ["symbolic-ref", "-q", "HEAD"], null, null, ct).ConfigureAwait(false);
        if (!symbolicRef.Succeeded)
        {
            return UnitResult<AgentError>.Failure(GitFailure("git symbolic-ref HEAD failed.", symbolicRef, secret: null));
        }

        if (!string.Equals(symbolicRef.StdOut.Trim(), expectedRef, StringComparison.Ordinal))
        {
            return UnitResult<AgentError>.Failure(AgentError.Validation(
                $"Refusing to push: the worktree's checked-out branch is not '{expectedRef}'."));
        }

        var (secretConfig, secret) = CredentialConfig(workspace.Remote);
        var refspec = $"{expectedRef}:{expectedRef}";
        var pushed = await _git.RunAsync(workspace.Root, ["push", "--end-of-options", workspace.Remote, refspec], null, secretConfig, ct).ConfigureAwait(false);
        return pushed.Succeeded
            ? UnitResult<AgentError>.Success()
            : UnitResult<AgentError>.Failure(GitFailure("git push failed.", pushed, secret));
    }

    /// <summary>
    /// The push's credential config for <paramref name="remoteUrl"/>, exactly as
    /// <see cref="GitWorktreeWorkspaceProvider"/> builds it: an <c>AUTHORIZATION</c> header carried through
    /// <see cref="GitCli.RunAsync"/>'s <c>secretConfig</c>, never <c>-c</c> and never disk. <see langword="null"/>
    /// credentials, or no <see cref="IGitCredentialSource"/> at all, means an anonymous push with no header.
    /// </summary>
    private (IReadOnlyList<(string Key, string Value)>? SecretConfig, string? Secret) CredentialConfig(string remoteUrl)
    {
        if (credentials?.GetCredentials(remoteUrl) is not { } creds)
        {
            return (null, null);
        }

        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{creds.Username}:{creds.Password}"));
        return ([("http.extraHeader", $"AUTHORIZATION: basic {token}")], token);
    }

    /// <summary>
    /// Builds the failure for a git command, scrubbing <paramref name="secret"/> out of the detail first — the same
    /// scrub <see cref="GitWorktreeWorkspaceProvider"/> applies — and logs it.
    /// </summary>
    private AgentError GitFailure(string message, GitCliResult result, string? secret)
    {
        if (result.TimedOut)
        {
            LogGitOperationFailed(logger, message, "timed out");
            return AgentError.GitOperationFailed($"{message} The git command timed out.");
        }

        var detail = Scrub(GitWorktreeWorkspaceProvider.ExtractErrorDetail(result.StdErr), secret);
        LogGitOperationFailed(logger, message, detail);
        return AgentError.GitOperationFailed(message, detail);
    }

    private static string Scrub(string text, string? secret) =>
        secret is null ? text : text.Replace(secret, "***", StringComparison.Ordinal);

    [LoggerMessage(EventId = 3000, Level = LogLevel.Warning, Message = "{Message} {Detail}")]
    private static partial void LogGitOperationFailed(ILogger logger, string message, string detail);
}
