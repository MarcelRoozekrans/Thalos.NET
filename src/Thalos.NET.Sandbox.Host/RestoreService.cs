using System.Globalization;
using Microsoft.Extensions.Logging;
using Thalos.Mcp;
using Thalos.Workspaces;

namespace Thalos.Sandbox.Host;

/// <summary>Where the run's package restore stands.</summary>
internal enum RestoreState
{
    /// <summary>Not run yet.</summary>
    Pending,

    /// <summary>The last restore exited 0.</summary>
    Ok,

    /// <summary>The last restore failed, timed out or could not start.</summary>
    Failed,
}

/// <summary>
/// Runs <c>dotnet restore &lt;solution&gt; --nologo -v:q</c> for the run, and again before the next build or Roslyn call
/// once a build file has changed.
/// </summary>
/// <remarks>
/// As an <see cref="IRunWorkspaceChangeListener"/>, the workspace tools tell it of every write. A changed file whose
/// name matches, case-insensitively, <c>*.csproj</c>, <c>*.props</c>, <c>*.targets</c>, <c>global.json</c> or
/// <c>NuGet.config</c> marks restore dirty, and <see cref="EnsureRestoredAsync"/> restores again before the call it
/// guards. Restores never overlap. A restore that a caller's cancellation cut short leaves restore dirty, so the next
/// call restores again. Every finished restore marks the Roslyn server for a reload, so the server's next lease loads
/// what the restore produced. Restore runs from <see cref="SandboxChildEnvironment.Curated"/>.
/// </remarks>
/// <param name="settings">The run id.</param>
/// <param name="workspaces">The run's workspace.</param>
/// <param name="runner">Runs <c>dotnet</c>.</param>
/// <param name="roslyn">Marked for a reload after every restore.</param>
/// <param name="logger">Logs each restore's outcome.</param>
internal sealed partial class RestoreService(
    SandboxSettings settings, LocalRunWorkspace workspaces, ISandboxProcessRunner runner, RunMcpServerRegistry roslyn, ILogger<RestoreService> logger)
    : IRunWorkspaceChangeListener, IDisposable
{
    /// <summary>How long one restore may run.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>How many characters of the end of a restore's output are kept for <c>/control/ready</c>.</summary>
    public const int DetailChars = 4096;

    private static readonly string[] BuildFileSuffixes = [".csproj", ".props", ".targets"];
    private static readonly string[] BuildFileNames = ["global.json", "NuGet.config"];
    private static readonly string[] RestoredMarker = ["(restore)"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private int _dirty;
    private RestoreState _state;
    private string? _detail;

    /// <summary>The last restore's state and the tail of its output, or null before the first.</summary>
    public (RestoreState State, string? Detail) Snapshot
    {
        get
        {
            lock (_sync)
            {
                return (_state, _detail);
            }
        }
    }

    /// <summary>Runs restore now, whatever the dirty flag says. The import calls it once, after the checkout.</summary>
    /// <param name="ct">Cancellation token; kills the restore.</param>
    public async Task RestoreAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref _dirty, 0);
            await RunAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Restores again when a build file changed since the last restore; otherwise returns at once. A call made while a restore runs waits for it.</summary>
    /// <param name="ct">Cancellation token; a restore it cuts short leaves restore dirty.</param>
    public async ValueTask EnsureRestoredAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _dirty, 0) == 1)
            {
                await RunAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void OnFilesChanged(Guid runId, IReadOnlyList<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        if (runId == settings.RunId && relativePaths.Any(IsBuildFile))
        {
            Interlocked.Exchange(ref _dirty, 1);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>Whether a change to <paramref name="relativePath"/> can change what restore resolves.</summary>
    internal static bool IsBuildFile(string relativePath)
    {
        var name = Path.GetFileName(relativePath.Replace('\\', '/').TrimEnd('/'));
        return Array.Exists(BuildFileSuffixes, s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase))
            || Array.Exists(BuildFileNames, n => string.Equals(name, n, StringComparison.OrdinalIgnoreCase));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        if (workspaces.Current is not { } workspace)
        {
            return;
        }

        List<string> arguments = ["restore", workspace.SolutionPath ?? workspace.Root, "--nologo", "-v:q"];
        ProcessOutcome outcome;
        try
        {
            outcome = await runner.RunAsync(
                new ProcessSpec("dotnet", arguments, workspace.Root, Timeout, Environment: SandboxChildEnvironment.Curated()), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Exchange(ref _dirty, 1); // cut short: whatever it left behind is not a restore
            throw;
        }

        var (state, detail) = Describe(outcome);
        lock (_sync)
        {
            _state = state;
            _detail = detail;
        }

        LogRestored(logger, state);

        // A build file written after a call's restore check but before its lease is reloaded by that lease, ahead of
        // this restore; marking the server again makes the next lease reload it over what this restore produced.
        roslyn.OnFilesChanged(settings.RunId, RestoredMarker);
    }

    private static (RestoreState State, string Detail) Describe(ProcessOutcome outcome)
    {
        if (outcome.StartError is not null)
        {
            return (RestoreState.Failed, $"dotnet restore could not start: {outcome.StartError}");
        }

        var tail = outcome.FullOutput.Length > DetailChars ? outcome.FullOutput[^DetailChars..] : outcome.FullOutput;
        if (outcome.TimedOut)
        {
            return (RestoreState.Failed, $"dotnet restore timed out after {Timeout:c}\n{tail}");
        }

        return outcome.ExitCode == 0
            ? (RestoreState.Ok, tail)
            : (RestoreState.Failed, $"dotnet restore exited {outcome.ExitCode!.Value.ToString(CultureInfo.InvariantCulture)}\n{tail}");
    }

    [LoggerMessage(EventId = 5912, Level = LogLevel.Information, Message = "Restore finished: {State}.")]
    private static partial void LogRestored(ILogger logger, RestoreState state);
}
