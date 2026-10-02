using Thalos.Git.Workspaces;
using Thalos.Mcp;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Sandbox;

public sealed partial class SandboxRunWorkspaceProvider
{
    // ---------- tools ----------

    /// <inheritdoc />
    /// <remarks>
    /// Only for a ready record whose container is running and belongs to the run; null otherwise, and null on any failure, which is logged:
    /// this never throws.
    /// </remarks>
    public async ValueTask<RunToolEndpoint?> ResolveAsync(Guid runId, string source, CancellationToken ct)
    {
        try
        {
            var read = await _store.ReadAsync(runId, ct).ConfigureAwait(false);
            if (read.Record is not { State: SandboxRecordState.Ready } record)
            {
                return null;
            }

            var handle = await runtime.GetAsync(record.SandboxId, ct).ConfigureAwait(false);
            return handle is { State: SandboxState.Running } && handle.RunId == runId
                ? new RunToolEndpoint(new Uri(handle.BaseAddress, $"mcp/{Uri.EscapeDataString(source)}"), record.Token)
                : null;
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
            {
                LogResolveFailed(logger, runId, ex.Message);
            }

            return null;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Asks the sandbox every two seconds. Succeeds once the import is done and Roslyn is ready; fails at once when
    /// Roslyn failed, with its detail, or when the container exited, with its exit code and whether it was killed for
    /// memory; fails when <paramref name="timeout"/> passes. A failed restore does not fail the wait: it is logged, and
    /// <see cref="ReadinessAsync"/> reports it.
    /// </remarks>
    public async ValueTask<UnitResult<AgentError>> WaitAllReadyAsync(Guid runId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = clock.GetUtcNow() + timeout;
        var last = "the sandbox has not answered yet";
        while (true)
        {
            var read = await _store.ReadAsync(runId, ct).ConfigureAwait(false);
            if (read.Record is not { State: SandboxRecordState.Ready } record)
            {
                return UnitResult<AgentError>.Failure(AgentError.Validation($"Run '{runId}' has no ready sandbox."));
            }

            var handle = await runtime.GetAsync(record.SandboxId, ct).ConfigureAwait(false);
            if (handle is not null && handle.RunId != runId)
            {
                return UnitResult<AgentError>.Failure(AgentError.ProviderError(OtherRun));
            }

            if (handle is { State: SandboxState.Exited or SandboxState.Missing } stopped)
            {
                return UnitResult<AgentError>.Failure(AgentError.ProviderError(Stopped(stopped)));
            }

            if (handle is null)
            {
                // Null is also how the runtime answers when the engine cannot be asked, so it is not yet a verdict.
                last = "the sandbox could not be found";
            }
            else
            {
                var (done, state) = await PollReadyAsync(runId, record, handle, ct).ConfigureAwait(false);
                if (done is { } verdict)
                {
                    return verdict;
                }

                last = state;
            }

            var remaining = deadline - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return UnitResult<AgentError>.Failure(AgentError.ProviderError($"The run's sandbox was not ready within {timeout}.", last));
            }

            await Task.Delay(remaining < ReadyPollInterval ? remaining : ReadyPollInterval, clock, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One question to the sandbox: a verdict once Roslyn is ready or failed, or none and the state to report if the wait
    /// times out.
    /// </summary>
    private async Task<(UnitResult<AgentError>? Verdict, string State)> PollReadyAsync(Guid runId, SandboxRecord record, SandboxHandle handle, CancellationToken ct)
    {
        var ready = await control.ReadyAsync(handle, record.Token, ct).ConfigureAwait(false);
        if (ready.IsFailure)
        {
            return (null, ready.Error.Message);
        }

        var readiness = ready.Value;
        if (string.Equals(readiness.Roslyn, "failed", StringComparison.Ordinal))
        {
            return (UnitResult<AgentError>.Failure(AgentError.ProviderError("The run's sandbox failed to start Roslyn.", LogSanitizer.Clean(readiness.Detail, 2000))), "");
        }

        if (readiness.Imported && string.Equals(readiness.Roslyn, "ready", StringComparison.Ordinal))
        {
            if (string.Equals(readiness.Restore, "failed", StringComparison.Ordinal))
            {
                LogRestoreFailed(logger, runId, LogSanitizer.Clean(readiness.RestoreDetail));
            }

            return (UnitResult<AgentError>.Success(), "");
        }

        return (null, $"import {(readiness.Imported ? "done" : "pending")}, restore {LogSanitizer.Clean(readiness.Restore, 16)}, roslyn {LogSanitizer.Clean(readiness.Roslyn, 16)}");
    }

    /// <summary>
    /// The run's sandbox's import, restore and Roslyn state, so a host can record a restore failure. Fails when the run
    /// has no ready sandbox, when its container is not running, or when the sandbox cannot be asked.
    /// </summary>
    /// <param name="runId">The run.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask<Result<SandboxReadiness, AgentError>> ReadinessAsync(Guid runId, CancellationToken ct)
    {
        var read = await _store.ReadAsync(runId, ct).ConfigureAwait(false);
        if (read.Record is not { State: SandboxRecordState.Ready } record)
        {
            return Result<SandboxReadiness, AgentError>.Failure(AgentError.Validation($"Run '{runId}' has no ready sandbox."));
        }

        var handle = await runtime.GetAsync(record.SandboxId, ct).ConfigureAwait(false);
        return handle switch
        {
            not null when handle.RunId != runId => Result<SandboxReadiness, AgentError>.Failure(AgentError.ProviderError(OtherRun)),
            null => Result<SandboxReadiness, AgentError>.Failure(AgentError.ProviderError("The run's sandbox could not be found.")),
            { State: SandboxState.Running } => await control.ReadyAsync(handle, record.Token, ct).ConfigureAwait(false),
            _ => Result<SandboxReadiness, AgentError>.Failure(AgentError.ProviderError(Stopped(handle))),
        };
    }

    /// <inheritdoc />
    /// <remarks>Reads the mirror at the record's base commit, never the sandbox, whatever the sandbox did to its copy.</remarks>
    public async ValueTask<Result<string?, AgentError>> ReadBaseFileAsync(Guid runId, string relativePath, CancellationToken ct)
    {
        var read = await _store.ReadAsync(runId, ct).ConfigureAwait(false);
        if (read.Record is not { State: SandboxRecordState.Ready or SandboxRecordState.Exporting or SandboxRecordState.Parked } record)
        {
            return Result<string?, AgentError>.Failure(AgentError.Validation($"Run '{runId}' has no workspace."));
        }

        if (!GitMirrorStore.IsValidRepositoryName(record.Repository))
        {
            return Result<string?, AgentError>.Failure(AgentError.Validation(
                $"The sandbox record of run '{runId}' names repository '{record.Repository}', which is not a valid mirror directory name."));
        }

        return await mirrors.ReadFileAsync(new GitMirror(record.Repository, mirrors.MirrorPath(record.Repository)), record.BaseCommit, relativePath, ct).ConfigureAwait(false);
    }


    /// <summary>The runtime answered for this run's sandbox id with a sandbox of another run: it is never talked to.</summary>
    private const string OtherRun = "The sandbox under this run's id belongs to another run.";

    private static string Stopped(SandboxHandle handle) => handle.State == SandboxState.Missing
        ? "the run's sandbox is gone"
        : $"the run's sandbox stopped (exit {handle.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}{(handle.OomKilled ? ", killed for memory" : "")})";
}
