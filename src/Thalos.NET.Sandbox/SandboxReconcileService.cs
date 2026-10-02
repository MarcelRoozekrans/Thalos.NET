using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Thalos.Sandbox;

/// <summary>
/// Runs <see cref="SandboxRunWorkspaceProvider.ReconcileAsync"/> once at boot, in the background, so a slow or unreachable
/// container engine never holds up the host's start. Stopping the host cancels it and waits for it to end.
/// </summary>
/// <param name="provider">The provider to reconcile.</param>
/// <param name="logger">Logs the outcome.</param>
internal sealed partial class SandboxReconcileService(SandboxRunWorkspaceProvider provider, ILogger<SandboxReconcileService> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private Task _run = Task.CompletedTask;

    /// <summary>Starts the reconcile and returns at once.</summary>
    /// <param name="cancellationToken">Ignored: the reconcile runs until done or until the host stops.</param>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _run = RunAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    /// <summary>Cancels the reconcile and waits for it, or for <paramref name="cancellationToken"/>.</summary>
    /// <param name="cancellationToken">The host's shutdown deadline.</param>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _run.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <inheritdoc />
    public void Dispose() => _stopping.Dispose();

    private async Task RunAsync(CancellationToken ct)
    {
        // Off the host's start path: StartAsync returns before the engine is asked anything.
        await Task.Yield();
        try
        {
            var deleted = await provider.ReconcileAsync(ct).ConfigureAwait(false);
            LogReconciled(logger, deleted);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The host is stopping.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing awaits this task but shutdown: a failure is logged here or nowhere.
            LogReconcileFailed(logger, ex.Message);
        }
    }

    [LoggerMessage(EventId = 2110, Level = LogLevel.Information, Message = "Sandbox reconcile finished; {Deleted} sandboxes or records deleted")]
    private static partial void LogReconciled(ILogger logger, int deleted);

    [LoggerMessage(EventId = 2111, Level = LogLevel.Warning, Message = "Sandbox reconcile failed: {Error}")]
    private static partial void LogReconcileFailed(ILogger logger, string error);
}
