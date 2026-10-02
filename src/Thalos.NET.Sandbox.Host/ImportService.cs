using Microsoft.Extensions.Logging;
using Thalos.Git.Workspaces;
using Thalos.Workspaces;

namespace Thalos.Sandbox.Host;

/// <summary>How <see cref="ImportService.StartAsync"/> answered.</summary>
internal enum ImportReply
{
    /// <summary>The bundle was stored and the import runs in the background: 202.</summary>
    Accepted,

    /// <summary>The branch, commit or solution is not acceptable: 400.</summary>
    Invalid,

    /// <summary>An import was already accepted: 409.</summary>
    AlreadyImported,

    /// <summary>The bundle is over <see cref="ImportService.MaxBundleBytes"/>: 413.</summary>
    TooLarge,

    /// <summary>The body could not be read to its end, such as a client that went away: 400.</summary>
    BodyFailed,
}

/// <summary>
/// The sandbox's one import: stores the run's bundle under the work volume, clones it into
/// <see cref="SandboxSettings.RepoRoot"/>, checks the run's commit out on the run's branch, restores, and starts Roslyn.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order.</b> The branch, commit and solution are checked before anything is written. The body is streamed to a
/// file under the work volume, never held in memory, and refused once it passes <see cref="MaxBundleBytes"/>. Only
/// then is the request answered, and the rest runs in the background: <c>git clone --no-checkout</c>, then
/// <c>git checkout -b &lt;branch&gt; &lt;commit&gt;</c>, both with <c>core.symlinks=false</c>, which the clone also persists
/// in the repository's config, the checkout only once <see cref="RepoConfigGuard"/> passed; then
/// <see cref="RestoreService.RestoreAsync"/>, then the workspace is published ready, which starts Roslyn, whether
/// restore succeeded or not. The bundle names its commit under a temporary <c>refs/heads/thalos-bundle/&lt;guid&gt;</c>
/// branch, so the commit is in the clone.
/// </para>
/// <para>
/// <b>Git isolation.</b> Every git command runs through <see cref="GitCli"/>, the same isolation the trusted host uses:
/// no system or global config, an isolated home, an empty hooks directory as <c>core.hooksPath</c>, no fsmonitor, and a
/// bounded timeout per command.
/// </para>
/// <para>
/// <b>Once.</b> A sandbox imports once. A second import is refused, even after the first failed: the API discards a
/// sandbox whose import failed. A refused, oversized or unreadable body releases the claim, since nothing was imported.
/// </para>
/// </remarks>
/// <param name="settings">The work root.</param>
/// <param name="git">Runs git in isolation.</param>
/// <param name="workspaces">Receives the checkout.</param>
/// <param name="restore">Restores after the checkout.</param>
/// <param name="roslyn">Waits for the Roslyn server and records its state.</param>
/// <param name="logger">Logs the import's outcome.</param>
internal sealed partial class ImportService(
    SandboxSettings settings, GitCli git, LocalRunWorkspace workspaces, RestoreService restore, RoslynProxyTools roslyn, ILogger<ImportService> logger)
    : IAsyncDisposable
{
    /// <summary>The largest bundle accepted: 512 MiB.</summary>
    public const long MaxBundleBytes = 512L * 1024 * 1024;

    private static readonly string[] CheckoutConfig = ["core.symlinks=false"];

    private readonly CancellationTokenSource _stopping = new();
    private int _claimed;
    private Task _import = Task.CompletedTask;
    private volatile string? _failure;

    /// <summary>Why the import failed, or null.</summary>
    public string? Failure => _failure;

    /// <summary>
    /// Checks the request, stores the bundle and starts the import in the background. Returns once the bundle is on
    /// disk; the import's progress shows in <c>/control/ready</c>.
    /// </summary>
    /// <param name="branch">The branch to create.</param>
    /// <param name="commit">The full 40-character sha to check out.</param>
    /// <param name="solution">The solution, relative to the repository root. Required.</param>
    /// <param name="body">The bundle.</param>
    /// <param name="ct">The request's cancellation; it never cancels the import once accepted.</param>
    public async Task<ImportReply> StartAsync(string? branch, string? commit, string? solution, Stream body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!GitMirrorStore.IsFullSha(commit ?? "") || !IsAcceptableSolution(solution) || !await IsValidBranchAsync(branch, ct).ConfigureAwait(false))
        {
            return ImportReply.Invalid;
        }

        if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
        {
            return ImportReply.AlreadyImported;
        }

        var bundle = Path.Combine(settings.WorkRoot, "import", $"{Guid.NewGuid():N}.bundle");
        var reply = ImportReply.BodyFailed;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(bundle)!);
            reply = await StoreAsync(body, bundle, MaxBundleBytes, ct).ConfigureAwait(false) ? ImportReply.Accepted : ImportReply.TooLarge;
        }
        catch (IOException)
        {
            // A client that went away, or a body Kestrel stopped reading: nothing was imported.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The request was aborted while the body streamed.
        }
        finally
        {
            if (reply != ImportReply.Accepted)
            {
                TryDelete(bundle);
                Volatile.Write(ref _claimed, 0);
            }
        }

        if (reply != ImportReply.Accepted)
        {
            return reply;
        }

        _import = RunAsync(bundle, branch!, commit!, solution!, _stopping.Token);
        return ImportReply.Accepted;
    }

    /// <summary>
    /// Copies <paramref name="source"/> to a new file at <paramref name="path"/>, stopping as soon as more than
    /// <paramref name="maxBytes"/> have arrived. Returns false when it stopped; the partial file is the caller's to delete.
    /// </summary>
    internal static async Task<bool> StoreAsync(Stream source, string path, long maxBytes, CancellationToken ct)
    {
        var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await using (target.ConfigureAwait(false))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    return false;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            return true;
        }
    }

    /// <summary>Stops a running import and waits for it to end.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _import.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stopping.Dispose();
    }

    private async Task RunAsync(string bundle, string branch, string commit, string solution, CancellationToken stopping)
    {
        try
        {
            var problem = await CheckOutAsync(bundle, branch, commit, solution, stopping).ConfigureAwait(false);
            TryDelete(bundle);
            if (problem is not null)
            {
                Fail(problem);
                return;
            }

            LogImported(logger, commit);
            await restore.RestoreAsync(stopping).ConfigureAwait(false);
            await workspaces.PublishReadyAsync(stopping).ConfigureAwait(false);
            await roslyn.WaitReadyAsync(stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The host is stopping; the container goes with it.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing awaits this task but shutdown, so an unexpected failure must still end in a terminal state.
            TryDelete(bundle);
            Fail(ex.Message);
        }
    }

    private void Fail(string problem)
    {
        _failure = $"import failed: {problem}";
        roslyn.Fail(_failure);
        LogImportFailed(logger, problem);
    }

    /// <summary>Clones and checks out; returns why it could not, or null once the workspace is published.</summary>
    private async Task<string?> CheckOutAsync(string bundle, string branch, string commit, string solution, CancellationToken ct)
    {
        var repo = settings.RepoRoot;
        Directory.CreateDirectory(repo);
        var cloned = await git.RunAsync(settings.WorkRoot, ["clone", "--no-checkout", "--config", "core.symlinks=false", "--", bundle, repo], CheckoutConfig, null, ct).ConfigureAwait(false);
        if (!cloned.Succeeded)
        {
            return Describe("git clone", cloned);
        }

        if (await RepoConfigGuard.CheckAsync(git, repo, ct).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var checkedOut = await git.RunAsync(repo, ["checkout", "-b", branch, commit], CheckoutConfig, null, ct).ConfigureAwait(false);
        if (!checkedOut.Succeeded)
        {
            return Describe("git checkout", checkedOut);
        }

        var resolved = WorkspacePath.Resolve(repo, solution);
        if (resolved.IsFailure || !File.Exists(resolved.Value))
        {
            return $"the solution '{solution}' is not a file in the repository";
        }

        workspaces.Publish(new RunWorkspace(settings.RunId, "sandbox", "", branch, branch, repo, resolved.Value) { BaseCommit = commit, CreatedAt = DateTimeOffset.UtcNow });
        return null;
    }

    private async Task<bool> IsValidBranchAsync(string? branch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(branch) || branch.StartsWith('-'))
        {
            return false;
        }

        Directory.CreateDirectory(settings.WorkRoot);
        var format = await git.RunAsync(settings.WorkRoot, ["check-ref-format", "--branch", branch], null, null, ct).ConfigureAwait(false);
        return format.Succeeded;
    }

    /// <summary>
    /// Required, because Roslyn is started on it; a relative path with no parent segment, which the checkout confines
    /// again with <see cref="WorkspacePath.Resolve"/>.
    /// </summary>
    private static bool IsAcceptableSolution([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? solution) =>
        !string.IsNullOrWhiteSpace(solution) && !Path.IsPathRooted(solution) && !solution.StartsWith('-')
            && !solution.Replace('\\', '/').Split('/').Contains("..", StringComparer.Ordinal);

    private static string Describe(string command, GitCliResult result) =>
        result.TimedOut ? $"{command} timed out" : $"{command} exited {result.ExitCode}: {GitMirrorStore.ExtractErrorDetail(result.StdErr)}";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind on the work volume, which is deleted with the sandbox.
        }
    }

    [LoggerMessage(EventId = 5915, Level = LogLevel.Information, Message = "Imported commit {Commit}.")]
    private static partial void LogImported(ILogger logger, string commit);

    [LoggerMessage(EventId = 5916, Level = LogLevel.Warning, Message = "Import failed: {Problem}")]
    private static partial void LogImportFailed(ILogger logger, string problem);
}
