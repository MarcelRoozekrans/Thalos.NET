using Thalos.Git.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Sandbox.Host;

/// <summary>
/// Exports the run's changes as one binary patch against the imported commit: <c>git add -A</c>, then
/// <c>git diff --cached --binary --full-index &lt;commit&gt;</c>. An empty patch means no change. <c>bin</c> and <c>obj</c>
/// directories at any depth are never staged nor diffed, whatever <c>.gitignore</c> or an index agent code wrote says, and <see cref="RepoConfigGuard"/> checks the
/// repository's config before each git command, because agent-run code can write it. The trusted side applies
/// it to its own clean worktree with <c>GitPatchApplier</c>, which treats every byte of it as adversarial; nothing here
/// is a control.
/// </summary>
/// <remarks>
/// The patch is written by git itself with <c>--output</c> to a file under the work volume, never read through
/// <see cref="GitCli"/>'s line-based capture, which would turn a carriage return into a line feed and corrupt binary and
/// CRLF content. External diff drivers and textconv filters are switched off, so the patch is git's own. Exports run
/// one at a time, since each stages the whole tree.
/// </remarks>
/// <param name="settings">The work root.</param>
/// <param name="git">Runs git in isolation.</param>
/// <param name="workspaces">The run's workspace.</param>
internal sealed class ExportService(SandboxSettings settings, GitCli git, LocalRunWorkspace workspaces) : IDisposable
{
    /// <summary>Build output is never exported, whatever the repository's <c>.gitignore</c> says.</summary>
    private static readonly string[] BuildOutputExcludes = [":(exclude,glob)**/bin/**", ":(exclude,glob)**/obj/**"];

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Writes the patch to a new file under the work volume and returns its path; the caller streams it and deletes it.
    /// Fails with <see cref="AgentErrorCode.Validation"/> before the import, and with
    /// <see cref="AgentErrorCode.ProviderError"/> when git fails.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Result<string, AgentError>> ExportAsync(CancellationToken ct)
    {
        if (workspaces.Current is not { BaseCommit: { } commit } workspace)
        {
            return Result<string, AgentError>.Failure(AgentError.Validation("Nothing has been imported."));
        }

        var directory = Path.Combine(settings.WorkRoot, "export");
        Directory.CreateDirectory(directory);
        var patch = Path.Combine(directory, $"{Guid.NewGuid():N}.patch");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await RepoConfigGuard.CheckAsync(git, workspace.Root, ct).ConfigureAwait(false) is { } refused)
            {
                return Result<string, AgentError>.Failure(AgentError.ProviderError("Export refused.", refused));
            }

            var staged = await git.RunAsync(workspace.Root, ["add", "-A", "--", ".", .. BuildOutputExcludes], RepoConfigGuard.CommandConfig, null, ct).ConfigureAwait(false);
            if (!staged.Succeeded)
            {
                return Failed("git add", staged, patch);
            }

            if (await RepoConfigGuard.CheckAsync(git, workspace.Root, ct).ConfigureAwait(false) is { } changed)
            {
                return Result<string, AgentError>.Failure(AgentError.ProviderError("Export refused.", changed));
            }

            var diffed = await git.RunAsync(
                workspace.Root,
                ["diff", "--cached", "--binary", "--full-index", "--no-ext-diff", "--no-textconv", $"--output={patch}", commit, "--", ".", .. BuildOutputExcludes],
                RepoConfigGuard.CommandConfig,
                null,
                ct).ConfigureAwait(false);
            return diffed.Succeeded ? Result<string, AgentError>.Success(patch) : Failed("git diff", diffed, patch);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private static Result<string, AgentError> Failed(string command, GitCliResult result, string patch)
    {
        try
        {
            File.Delete(patch);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left on the work volume, which is deleted with the sandbox.
        }

        var detail = result.TimedOut ? "timed out" : GitMirrorStore.ExtractErrorDetail(result.StdErr);
        return Result<string, AgentError>.Failure(AgentError.ProviderError($"{command} failed during export.", detail));
    }
}
