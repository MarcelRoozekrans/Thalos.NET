using Microsoft.Extensions.Logging;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Sandbox.Host;

/// <summary>
/// The sandbox's one run workspace, at <see cref="SandboxSettings.RepoRoot"/>. It exists once
/// <see cref="ImportService"/> has checked the run's commit out; before that, <see cref="FindAsync"/> finds nothing
/// and every tool reports that the turn has no run workspace. A sandbox serves exactly one run, so a lookup for any
/// other run id finds nothing.
/// </summary>
/// <remarks>
/// The workspace is created and removed with its container, never through this provider: <see cref="CreateAsync"/>
/// and <see cref="RemoveAsync"/> always fail.
/// </remarks>
/// <param name="settings">The run id and work root.</param>
/// <param name="observers">Told when the workspace is ready: the Roslyn server registry.</param>
/// <param name="logger">Logs an observer that failed.</param>
internal sealed partial class LocalRunWorkspace(SandboxSettings settings, IEnumerable<IRunWorkspaceObserver> observers, ILogger<LocalRunWorkspace> logger)
    : IRunWorkspaceProvider
{
    private RunWorkspace? _workspace;

    /// <summary>The workspace once imported, or null.</summary>
    public RunWorkspace? Current => Volatile.Read(ref _workspace);

    /// <summary>Makes the imported checkout the run's workspace. Called once, by the import.</summary>
    /// <param name="workspace">The checkout.</param>
    public void Publish(RunWorkspace workspace) => Volatile.Write(ref _workspace, workspace);

    /// <summary>
    /// Tells every observer the workspace is ready, which starts the run's Roslyn server. Called once restore has
    /// finished, whether it succeeded or not. An observer that throws is logged and the others are still told.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task PublishReadyAsync(CancellationToken ct)
    {
        if (Current is not { } workspace)
        {
            return;
        }

        foreach (var observer in observers)
        {
            try
            {
                await observer.OnReadyAsync(workspace, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogObserverFailed(logger, ex, observer.GetType().Name);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<Result<RunWorkspace, AgentError>> CreateAsync(RunWorkspaceRequest request, CancellationToken ct) =>
        new(Result<RunWorkspace, AgentError>.Failure(AgentError.Validation("A sandbox's workspace is created by importing a bundle, not through the provider.")));

    /// <inheritdoc />
    public ValueTask<RunWorkspace?> FindAsync(Guid runId, CancellationToken ct) =>
        new(runId == settings.RunId ? Current : null);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<RunWorkspace>> ListAsync(CancellationToken ct) =>
        new(Current is { } workspace ? [workspace] : []);

    /// <inheritdoc />
    public ValueTask<UnitResult<AgentError>> RemoveAsync(Guid runId, CancellationToken ct) =>
        new(UnitResult<AgentError>.Failure(AgentError.Validation("A sandbox's workspace is removed with its container, not through the provider.")));

    [LoggerMessage(EventId = 5911, Level = LogLevel.Warning, Message = "Workspace observer {Observer} failed on ready.")]
    private static partial void LogObserverFailed(ILogger logger, Exception exception, string observer);
}
