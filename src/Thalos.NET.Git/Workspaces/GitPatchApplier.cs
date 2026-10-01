using Microsoft.Extensions.Logging;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Git.Workspaces;

/// <summary>How large a patch <see cref="GitPatchApplier"/> accepts.</summary>
/// <param name="MaxPatchBytes">The largest patch file, in bytes. A longer one is refused before git runs.</param>
/// <param name="MaxFiles">The most files one patch may touch. A patch over it is refused before anything is written.</param>
public sealed record PatchApplyLimits(long MaxPatchBytes = 16 * 1024 * 1024, int MaxFiles = 2000);

/// <summary>
/// The publish-side guard for a sandboxed run (security invariant S5): applies the run's patch, produced inside the
/// sandbox by <c>git diff --cached --binary --full-index &lt;base&gt;</c>, to the trusted host's own clean worktree,
/// and refuses it when it touches a protected path. This check is the control; the sandbox-side refusal is only a
/// convenience. Every byte of the patch was written by code an agent controlled, so it is treated as adversarial.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order.</b> The patch path must name a regular file — not a directory, link, FIFO or device. It is copied into a
/// private file under <see cref="GitWorkspaceOptions.DataRoot"/>, and refused when it is longer than
/// <see cref="PatchApplyLimits.MaxPatchBytes"/>. Every later step reads that copy, never the caller's file, so nothing
/// that can still write the caller's file (a sandbox volume, say) can swap the content between the check and the
/// apply. An empty copy is a run with no changes: nothing is applied and the re-check runs alone. Otherwise git lists
/// the paths the patch touches, the paths are checked, and only then is anything applied.
/// </para>
/// <para>
/// <b>Both sides of a rename or copy.</b> <c>git apply --numstat -z</c> prints one name per file: the new name, or
/// the old one for a deletion. For a rename or copy it does not print the old name at all, so a rename out of a
/// protected path would show only its harmless destination. The listing therefore runs twice, the second time with
/// <c>--reverse</c>: reversing a patch swaps every old and new name, so the second listing prints each old name. The
/// union of the two is every path the patch reads or writes, parsed by git itself rather than by a second parser here
/// that an adversarial patch could make disagree with git's.
/// </para>
/// <para>
/// <b>Path rules.</b> Besides <see cref="ProtectedPathSet"/>, a path is refused when it is empty or holds a line
/// break; leaves the worktree (a leading <c>/</c> or <c>\</c>, a <c>..</c> segment, a <c>:</c> or drive prefix); has a
/// <c>.git</c> segment at any depth; ends in <c>.gitattributes</c> or <c>.gitmodules</c> at any depth, since a nested
/// <c>.gitattributes</c> has the same filter power over its subtree; has a segment shaped like a Windows 8.3 short
/// name; or lies beyond a symlink or submodule the index already holds. Git's own <c>verify_path</c> refuses several
/// of these too; the applier does not rely on it. On an NTFS volume with 8.3 names, <c>GITHUB~1</c> is another name
/// for an existing <c>.github</c>: git writes <c>GITHUB~1/evil.yml</c> into <c>.github</c> while the index records the
/// short name, and git itself guards only the short names of <c>.git</c>. Every git call also carries
/// <c>core.protectNTFS</c> and <c>core.protectHFS</c>.
/// </para>
/// <para>
/// <b>Already applied.</b> A patch counts as already applied only when the index already differs from the base commit
/// and the patch applies in reverse. On an unchanged base the patch is always applied forward: a reverse check alone
/// passes on repetitive content the patch never touched — adding one line to a run of identical lines reverses
/// cleanly against the base — and would drop the change silently.
/// </para>
/// <para>
/// <b>Re-check.</b> After applying, the staged entries against the base commit, both sides of every rename, and every
/// name <c>git status</c> reports in the worktree, untracked and ignored included, are checked again, and so are the
/// staged modes: an entry that becomes a symlink (<c>120000</c>) or submodule (<c>160000</c>) is refused, type
/// changes included. The worktree scan catches a file that reached a protected path on disk under a name the index
/// does not show. On any failure of the re-check, a refusal or a listing that could not be read, the worktree is reset
/// to the base commit, or to HEAD when the base cannot be reached, and cleaned.
/// </para>
/// <para>
/// <b>Line breaks.</b> <see cref="GitCli"/> reads output line by line, which turns a carriage return in a file name
/// into a line feed. A name that comes back with a line break cannot be reported faithfully, so it is refused.
/// </para>
/// </remarks>
/// <param name="options">Where the private patch copy lives, and how git is run.</param>
/// <param name="logger">Required: every host has one.</param>
public sealed partial class GitPatchApplier(GitWorkspaceOptions options, ILogger<GitPatchApplier> logger)
{
    private static readonly string[] GitConfig = ["core.symlinks=false", "core.protectNTFS=true", "core.protectHFS=true"];

    private readonly GitCli _git = new(options);

    /// <summary>
    /// Applies the patch to the workspace's worktree with git apply --index --binary. Refuses, before anything is written, a patch
    /// over the limits or touching a protected path on either side of a rename; re-checks the staged names afterwards. Returns the
    /// staged paths. Idempotent: an already-applied patch succeeds without applying twice.
    /// </summary>
    public async Task<Result<IReadOnlyList<string>, AgentError>> ApplyAsync(
        RunWorkspace workspace, string patchPath, ProtectedPathSet protectedPaths, PatchApplyLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(patchPath);
        ArgumentNullException.ThrowIfNull(protectedPaths);
        ArgumentNullException.ThrowIfNull(limits);

        if (!IsFullSha(workspace.BaseCommit))
        {
            return Fail(AgentError.Validation($"The workspace of run '{workspace.RunId}' has no full base commit; publish refused."));
        }

        var copy = await CopyPatchAsync(patchPath, limits.MaxPatchBytes, ct).ConfigureAwait(false);
        if (copy.IsFailure)
        {
            return Fail(copy.Error);
        }

        try
        {
            return new FileInfo(copy.Value).Length == 0
                ? await RecheckAsync(workspace, protectedPaths, ct).ConfigureAwait(false)
                : await ApplyCopyAsync(workspace, copy.Value, protectedPaths, limits, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(copy.Value);
        }
    }

    /// <summary>
    /// Re-checks what the worktree holds against <paramref name="protectedPaths"/>: the staged entries against the base
    /// commit, both sides of a rename, their modes, and every name <c>git status</c> reports. On any failure the
    /// worktree is reset and cleaned. Returns the staged names. Internal so the re-check can be tested on its own,
    /// since no patch that passes the earlier checks is known to reach it.
    /// </summary>
    internal async Task<Result<IReadOnlyList<string>, AgentError>> RecheckAsync(RunWorkspace workspace, ProtectedPathSet protectedPaths, CancellationToken ct)
    {
        if (workspace.BaseCommit is not { } baseCommit || !IsFullSha(baseCommit))
        {
            return Fail(AgentError.Validation($"The workspace of run '{workspace.RunId}' has no full base commit; publish refused."));
        }

        var problem = await FindRecheckProblemAsync(workspace.Root, baseCommit, protectedPaths, ct).ConfigureAwait(false);
        if (problem.IsSuccess)
        {
            return problem;
        }

        LogRefused(logger, workspace.RunId, problem.Error.Message);
        await ResetAsync(workspace.Root, baseCommit, ct).ConfigureAwait(false);
        return problem;
    }

    /// <summary>The staged names when the re-check passes; otherwise why it did not.</summary>
    private async Task<Result<IReadOnlyList<string>, AgentError>> FindRecheckProblemAsync(string root, string baseCommit, ProtectedPathSet protectedPaths, CancellationToken ct)
    {
        var raw = await RunAsync(root, ["diff", "--cached", "--raw", "-z", "--no-renames", baseCommit], "list the staged entries", ct).ConfigureAwait(false);
        if (raw.IsFailure)
        {
            return Fail(raw.Error);
        }

        var status = await RunAsync(root, ["status", "--porcelain", "-z", "--untracked-files=all", "--ignored", "--no-renames"], "list the worktree status", ct).ConfigureAwait(false);
        if (status.IsFailure)
        {
            return Fail(status.Error);
        }

        var entries = ParseRaw(raw.Value);
        if (entries is null)
        {
            return Fail(AgentError.Validation("git diff --raw printed output that could not be parsed; publish refused."));
        }

        var names = entries.Select(e => e.Path).ToList();
        var refusal = FindLinkModeRefusal(entries)
            ?? FindRefusal(names, protectedPaths)
            ?? FindRefusal(StatusNames(SplitNul(status.Value)), protectedPaths);
        return refusal is null
            ? Result<IReadOnlyList<string>, AgentError>.Success(names)
            : Fail(AgentError.Validation(refusal));
    }

    private async Task<Result<IReadOnlyList<string>, AgentError>> ApplyCopyAsync(
        RunWorkspace workspace, string copy, ProtectedPathSet protectedPaths, PatchApplyLimits limits, CancellationToken ct)
    {
        var touched = await ReadTouchedPathsAsync(workspace.Root, copy, limits.MaxFiles, ct).ConfigureAwait(false);
        if (touched.IsFailure)
        {
            return touched;
        }

        var links = await RunAsync(workspace.Root, ["ls-files", "-s", "-z"], "list the index", ct).ConfigureAwait(false);
        if (links.IsFailure)
        {
            return Fail(links.Error);
        }

        var refusal = FindRefusal(touched.Value, protectedPaths, LinkPaths(links.Value));
        if (refusal is not null)
        {
            LogRefused(logger, workspace.RunId, refusal);
            return Fail(AgentError.Validation(refusal));
        }

        var applied = await ApplyUnlessAlreadyAppliedAsync(workspace.Root, copy, workspace.BaseCommit!, ct).ConfigureAwait(false);
        return applied.IsFailure ? Fail(applied.Error) : await RecheckAsync(workspace, protectedPaths, ct).ConfigureAwait(false);
    }

    /// <summary>Every path the patch touches, old and new sides alike — see the class remarks on renames and copies.</summary>
    private async Task<Result<IReadOnlyList<string>, AgentError>> ReadTouchedPathsAsync(string root, string copy, int maxFiles, CancellationToken ct)
    {
        var forward = await RunAsync(root, ["apply", "--numstat", "-z", copy], "read the patch", ct).ConfigureAwait(false);
        if (forward.IsFailure)
        {
            return Fail(forward.Error);
        }

        var newNames = ParseNumstat(forward.Value);
        if (newNames is null)
        {
            return Fail(AgentError.Validation("git apply --numstat printed output that could not be parsed; publish refused."));
        }

        if (newNames.Count > maxFiles)
        {
            return Fail(AgentError.Validation($"The patch touches {newNames.Count} files, more than the limit of {maxFiles}; publish refused."));
        }

        var reverse = await RunAsync(root, ["apply", "--numstat", "-z", "--reverse", copy], "read the patch in reverse", ct).ConfigureAwait(false);
        if (reverse.IsFailure)
        {
            return Fail(reverse.Error);
        }

        var oldNames = ParseNumstat(reverse.Value);
        if (oldNames is null)
        {
            return Fail(AgentError.Validation("git apply --numstat --reverse printed output that could not be parsed; publish refused."));
        }

        return Result<IReadOnlyList<string>, AgentError>.Success([.. newNames.Concat(oldNames).Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>Applies the patch forward, unless the index already differs from the base and the patch applies in reverse — see the class remarks.</summary>
    private async Task<UnitResult<AgentError>> ApplyUnlessAlreadyAppliedAsync(string root, string copy, string baseCommit, CancellationToken ct)
    {
        var differs = await _git.RunAsync(root, ["diff", "--cached", "--quiet", baseCommit], GitConfig, null, ct).ConfigureAwait(false);
        if (differs.TimedOut || differs.ExitCode is not (0 or 1))
        {
            return UnitResult<AgentError>.Failure(GitMirrorStore.GitFailure("Could not compare the index with the base commit.", differs, secret: null));
        }

        if (differs.ExitCode == 1)
        {
            var reverseCheck = await _git.RunAsync(root, ["apply", "--index", "--check", "--reverse", copy], GitConfig, null, ct).ConfigureAwait(false);
            if (reverseCheck.TimedOut)
            {
                return UnitResult<AgentError>.Failure(GitMirrorStore.GitFailure("Could not check whether the patch is already applied.", reverseCheck, secret: null));
            }

            if (reverseCheck.Succeeded)
            {
                return UnitResult<AgentError>.Success();
            }
        }

        var applied = await _git.RunAsync(root, ["apply", "--index", "--binary", "--whitespace=nowarn", copy], GitConfig, null, ct).ConfigureAwait(false);
        return applied.Succeeded
            ? UnitResult<AgentError>.Success()
            : UnitResult<AgentError>.Failure(GitMirrorStore.GitFailure("git apply failed.", applied, secret: null));
    }

    /// <summary>Resets the worktree to <paramref name="baseCommit"/>, or to HEAD when that fails, then cleans it, ignored files included.</summary>
    private async Task ResetAsync(string root, string baseCommit, CancellationToken ct)
    {
        var reset = await _git.RunAsync(root, ["reset", "--hard", "-q", baseCommit], GitConfig, null, ct).ConfigureAwait(false);
        if (!reset.Succeeded)
        {
            LogCleanupFailed(logger, "reset the worktree to the base commit", GitWorktreeWorkspaceProvider.ExtractErrorDetail(reset.StdErr));
            var toHead = await _git.RunAsync(root, ["reset", "--hard", "-q", "HEAD"], GitConfig, null, ct).ConfigureAwait(false);
            if (!toHead.Succeeded)
            {
                LogCleanupFailed(logger, "reset the worktree to HEAD", GitWorktreeWorkspaceProvider.ExtractErrorDetail(toHead.StdErr));
            }
        }

        var clean = await _git.RunAsync(root, ["clean", "-ffdxq"], GitConfig, null, ct).ConfigureAwait(false);
        if (!clean.Succeeded)
        {
            LogCleanupFailed(logger, "clean the worktree", GitWorktreeWorkspaceProvider.ExtractErrorDetail(clean.StdErr));
        }
    }

    private async Task<Result<string, AgentError>> RunAsync(string root, IReadOnlyList<string> args, string what, CancellationToken ct)
    {
        var result = await _git.RunAsync(root, args, GitConfig, null, ct).ConfigureAwait(false);
        return result.Succeeded
            ? Result<string, AgentError>.Success(result.StdOut)
            : Result<string, AgentError>.Failure(GitMirrorStore.GitFailure($"Could not {what}.", result, secret: null));
    }

    /// <summary>
    /// Copies the patch into a private file under <see cref="GitWorkspaceOptions.DataRoot"/>, refusing it when the path
    /// is not a regular file, or once more than <paramref name="maxBytes"/> have been read — the length is counted while
    /// copying, never taken from file metadata, so a file still growing cannot slip past it.
    /// </summary>
    private async Task<Result<string, AgentError>> CopyPatchAsync(string patchPath, long maxBytes, CancellationToken ct)
    {
        if (!IsRegularFile(patchPath))
        {
            return Result<string, AgentError>.Failure(AgentError.Validation($"The patch '{patchPath}' is not a regular file; publish refused."));
        }

        var directory = Path.Combine(Path.GetFullPath(options.DataRoot), "patches");
        var copy = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".patch");
        try
        {
            Directory.CreateDirectory(directory);
            if (await CopyBoundedAsync(patchPath, copy, maxBytes, ct).ConfigureAwait(false))
            {
                return Result<string, AgentError>.Success(copy);
            }

            TryDelete(copy);
            return Result<string, AgentError>.Failure(AgentError.Validation($"The patch is larger than the limit of {maxBytes} bytes; publish refused."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(copy);
            return Result<string, AgentError>.Failure(AgentError.StoreError($"Could not read the patch '{patchPath}'.", ex.Message));
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> is a regular file, not following a final link. On Linux the file type comes from
    /// <c>statx</c>, which tells a FIFO or device — that would block or never end on open — from a regular file; an
    /// unreadable type fails closed. Elsewhere, a directory, link or device attribute refuses it.
    /// </summary>
    private static bool IsRegularFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null
            || info.Attributes.HasFlag(FileAttributes.Directory)
            || info.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || info.Attributes.HasFlag(FileAttributes.Device))
        {
            return false;
        }

        return !OperatingSystem.IsLinux() || UnixLinkCount.IsRegularFile(path) == true;
    }

    /// <summary>Copies <paramref name="source"/> to <paramref name="target"/>; <see langword="false"/> once more than <paramref name="maxBytes"/> are read.</summary>
    private static async Task<bool> CopyBoundedAsync(string source, string target, long maxBytes, CancellationToken ct)
    {
        var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var inputDisposal = input.ConfigureAwait(false);
        var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await using var outputDisposal = output.ConfigureAwait(false);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                return false;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        return true;
    }

    private static Result<IReadOnlyList<string>, AgentError> Fail(AgentError error) => Result<IReadOnlyList<string>, AgentError>.Failure(error);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a stray copy under DataRoot/patches is never read again, since every apply makes a new one.
        }
    }

    [LoggerMessage(EventId = 1100, Level = LogLevel.Warning, Message = "Refused the patch for run {RunId}: {Reason}")]
    private static partial void LogRefused(ILogger logger, Guid runId, string reason);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Warning, Message = "Could not {What}: {Error}")]
    private static partial void LogCleanupFailed(ILogger logger, string what, string error);
}
