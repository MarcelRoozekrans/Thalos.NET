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
/// <b>Order.</b> The patch is first copied into a private file under <see cref="GitWorkspaceOptions.DataRoot"/>, and
/// the copy is refused when it is longer than <see cref="PatchApplyLimits.MaxPatchBytes"/>. Every later step reads
/// that copy, never the caller's file, so nothing that can still write the caller's file (a sandbox volume, say) can
/// swap the content between the check and the apply. Git then lists the paths the patch touches, the paths are
/// checked, and only then is anything applied.
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
/// <b>Windows short names.</b> On an NTFS volume with 8.3 names, <c>GITHUB~1</c> is another name for an existing
/// <c>.github</c> directory: a patch creating <c>GITHUB~1/evil.yml</c> passes a name check and git writes the file
/// into <c>.github</c>, while the index records the short name. Git itself guards only the short names of
/// <c>.git</c>. Any path with a segment shaped like a short name (<c>~</c>, digits, then the end or a <c>.</c>) is
/// refused on every OS. Trailing dots and spaces and <c>:</c> streams are not checked here: git for Windows already
/// refuses those paths as invalid, and on other systems they name distinct files.
/// </para>
/// <para>
/// <b>Re-check.</b> After applying, the staged names against the base commit, both sides of every rename, and every
/// name <c>git status</c> reports in the worktree, untracked and ignored included, are checked again. The worktree
/// scan catches a file that reached a protected path on disk under a name the index does not show. On a hit the
/// worktree is reset to the base commit and cleaned, and the apply fails.
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
    private static readonly string[] SymlinksOff = ["core.symlinks=false"];

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
            return await ApplyCopyAsync(workspace, copy.Value, protectedPaths, limits, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(copy.Value);
        }
    }

    /// <summary>
    /// Re-checks what the worktree holds against <paramref name="protectedPaths"/>: the staged names against the base
    /// commit, both sides of a rename, and every name <c>git status</c> reports. On a hit the worktree is reset to the
    /// base commit and cleaned. Returns the staged names. Internal so the re-check can be tested on its own, since no
    /// patch that passes the earlier checks is known to reach it.
    /// </summary>
    internal async Task<Result<IReadOnlyList<string>, AgentError>> RecheckAsync(RunWorkspace workspace, ProtectedPathSet protectedPaths, CancellationToken ct)
    {
        if (workspace.BaseCommit is not { } baseCommit || !IsFullSha(baseCommit))
        {
            return Fail(AgentError.Validation($"The workspace of run '{workspace.RunId}' has no full base commit; publish refused."));
        }

        var staged = await ListAsync(workspace.Root, ["diff", "--cached", "--name-only", "-z", "--no-renames", baseCommit], "list the staged names", ct).ConfigureAwait(false);
        if (staged.IsFailure)
        {
            return staged;
        }

        var status = await ListAsync(workspace.Root, ["status", "--porcelain", "-z", "--untracked-files=all", "--ignored", "--no-renames"], "list the worktree status", ct).ConfigureAwait(false);
        if (status.IsFailure)
        {
            return status;
        }

        var refusal = FindRefusal(staged.Value, protectedPaths) ?? FindRefusal(StatusNames(status.Value), protectedPaths);
        if (refusal is null)
        {
            return staged;
        }

        LogRefused(logger, workspace.RunId, refusal);
        await ResetAsync(workspace, ct).ConfigureAwait(false);
        return Fail(AgentError.Validation(refusal));
    }

    private async Task<Result<IReadOnlyList<string>, AgentError>> ApplyCopyAsync(
        RunWorkspace workspace, string copy, ProtectedPathSet protectedPaths, PatchApplyLimits limits, CancellationToken ct)
    {
        var touched = await ReadTouchedPathsAsync(workspace.Root, copy, limits.MaxFiles, ct).ConfigureAwait(false);
        if (touched.IsFailure)
        {
            return touched;
        }

        var refusal = FindRefusal(touched.Value, protectedPaths);
        if (refusal is not null)
        {
            LogRefused(logger, workspace.RunId, refusal);
            return Fail(AgentError.Validation(refusal));
        }

        var applied = await ApplyUnlessAlreadyAppliedAsync(workspace.Root, copy, ct).ConfigureAwait(false);
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

    private async Task<UnitResult<AgentError>> ApplyUnlessAlreadyAppliedAsync(string root, string copy, CancellationToken ct)
    {
        var reverseCheck = await _git.RunAsync(root, ["apply", "--index", "--check", "--reverse", copy], SymlinksOff, null, ct).ConfigureAwait(false);
        if (reverseCheck.TimedOut)
        {
            return UnitResult<AgentError>.Failure(GitMirrorStore.GitFailure("Could not check whether the patch is already applied.", reverseCheck, secret: null));
        }

        if (reverseCheck.Succeeded)
        {
            return UnitResult<AgentError>.Success();
        }

        var applied = await _git.RunAsync(root, ["apply", "--index", "--binary", "--whitespace=nowarn", copy], SymlinksOff, null, ct).ConfigureAwait(false);
        return applied.Succeeded
            ? UnitResult<AgentError>.Success()
            : UnitResult<AgentError>.Failure(GitMirrorStore.GitFailure("git apply failed.", applied, secret: null));
    }

    private async Task ResetAsync(RunWorkspace workspace, CancellationToken ct)
    {
        var reset = await _git.RunAsync(workspace.Root, ["reset", "--hard", "-q", workspace.BaseCommit!], SymlinksOff, null, ct).ConfigureAwait(false);
        if (!reset.Succeeded)
        {
            LogCleanupFailed(logger, "reset the worktree to the base commit", GitWorktreeWorkspaceProvider.ExtractErrorDetail(reset.StdErr));
        }

        var clean = await _git.RunAsync(workspace.Root, ["clean", "-ffdxq"], SymlinksOff, null, ct).ConfigureAwait(false);
        if (!clean.Succeeded)
        {
            LogCleanupFailed(logger, "clean the worktree", GitWorktreeWorkspaceProvider.ExtractErrorDetail(clean.StdErr));
        }
    }

    private async Task<Result<IReadOnlyList<string>, AgentError>> ListAsync(string root, IReadOnlyList<string> args, string what, CancellationToken ct)
    {
        var output = await RunAsync(root, args, what, ct).ConfigureAwait(false);
        return output.IsFailure
            ? Fail(output.Error)
            : Result<IReadOnlyList<string>, AgentError>.Success(SplitNul(output.Value));
    }

    private async Task<Result<string, AgentError>> RunAsync(string root, IReadOnlyList<string> args, string what, CancellationToken ct)
    {
        var result = await _git.RunAsync(root, args, SymlinksOff, null, ct).ConfigureAwait(false);
        return result.Succeeded
            ? Result<string, AgentError>.Success(result.StdOut)
            : Result<string, AgentError>.Failure(GitMirrorStore.GitFailure($"Could not {what}.", result, secret: null));
    }

    /// <summary>
    /// Copies the patch into a private file under <see cref="GitWorkspaceOptions.DataRoot"/>, refusing it once more
    /// than <paramref name="maxBytes"/> have been read — the length is counted while copying, never taken from file
    /// metadata, so a link or a file still growing cannot slip past it.
    /// </summary>
    private async Task<Result<string, AgentError>> CopyPatchAsync(string patchPath, long maxBytes, CancellationToken ct)
    {
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

    /// <summary>
    /// The names in <c>git apply --numstat -z</c> output: <c>added\tdeleted\tname\0</c> per file, or
    /// <c>added\tdeleted\t\0old\0new\0</c> should git ever print both sides of a rename the way <c>git diff</c> does.
    /// Binary files print <c>-</c> for both counts. <see langword="null"/> when a record is malformed.
    /// </summary>
    internal static List<string>? ParseNumstat(string output)
    {
        var fields = SplitNul(output);
        var names = new List<string>();
        for (var i = 0; i < fields.Count; i++)
        {
            var record = fields[i];
            var first = record.IndexOf('\t', StringComparison.Ordinal);
            var second = first < 0 ? -1 : record.IndexOf('\t', first + 1);
            if (second < 0 || !IsCount(record.AsSpan(0, first)) || !IsCount(record.AsSpan(first + 1, second - first - 1)))
            {
                return null;
            }

            var name = record[(second + 1)..];
            if (name.Length > 0)
            {
                names.Add(name);
                continue;
            }

            if (i + 2 >= fields.Count)
            {
                return null;
            }

            names.Add(fields[++i]);
            names.Add(fields[++i]);
        }

        return names;
    }

    /// <summary>The paths in <c>git status --porcelain -z --no-renames</c> output, each record <c>XY path</c>.</summary>
    private static IEnumerable<string> StatusNames(IReadOnlyList<string> records) =>
        records.Select(record => record.Length > 3 ? record[3..] : record);

    /// <summary>
    /// Splits NUL-terminated output into its fields. <see cref="GitCli"/> appends a line feed after the last line it
    /// reads, so one trailing line feed is dropped first; an empty last field after the final NUL is not a field.
    /// </summary>
    private static List<string> SplitNul(string output)
    {
        var text = output.EndsWith('\n') ? output[..^1] : output;
        var fields = new List<string>(text.Split('\0'));
        if (fields.Count > 0 && fields[^1].Length == 0)
        {
            fields.RemoveAt(fields.Count - 1);
        }

        return fields;
    }

    /// <summary>Why <paramref name="paths"/> must not be published, or <see langword="null"/> when nothing in it is refused.</summary>
    internal static string? FindRefusal(IEnumerable<string> paths, ProtectedPathSet protectedPaths)
    {
        foreach (var path in paths)
        {
            if (path.Length == 0 || path.Contains('\n', StringComparison.Ordinal) || path.Contains('\r', StringComparison.Ordinal))
            {
                return "the change touches a path that is empty or holds a line break; publish refused";
            }

            if (protectedPaths.IsProtected(path))
            {
                return $"the change touches protected path '{path}'; publish refused";
            }

            if (HasShortNameSegment(path))
            {
                return $"the change touches path '{path}', which can name another file on Windows; publish refused";
            }
        }

        return null;
    }

    /// <summary>
    /// <see langword="true"/> when a segment of <paramref name="path"/> is shaped like a Windows 8.3 short name: a
    /// <c>~</c>, one or more digits, then the end of the segment or a <c>.</c> — <c>GITHUB~1</c>, <c>GITATT~1</c>.
    /// </summary>
    internal static bool HasShortNameSegment(string path)
    {
        for (var tilde = path.IndexOf('~', StringComparison.Ordinal); tilde >= 0; tilde = path.IndexOf('~', tilde + 1))
        {
            var end = tilde + 1;
            while (end < path.Length && char.IsAsciiDigit(path[end]))
            {
                end++;
            }

            if (end > tilde + 1 && (end == path.Length || path[end] is '.' or '/' or (char)92))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCount(ReadOnlySpan<char> value)
    {
        if (value is "-")
        {
            return true;
        }

        if (value.IsEmpty)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFullSha(string? value)
    {
        if (value is not { Length: 40 })
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f'))
            {
                return false;
            }
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
