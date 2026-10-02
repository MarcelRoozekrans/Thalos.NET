using ZeroAlloc.Results;

namespace Thalos.Sandbox;

/// <summary>Creates, finds and deletes per-run sandboxes.</summary>
public interface ISandboxRuntime
{
    /// <summary>Creates and starts a sandbox. Fails, never throws, when the engine is unreachable or the image is missing.</summary>
    /// <remarks>
    /// A run has at most one create in flight. Implementations may serialise creates and deletes per sandbox id, and a
    /// create that finds another create's container fails without touching it.
    /// </remarks>
    ValueTask<Result<SandboxHandle, AgentError>> CreateAsync(SandboxSpec spec, CancellationToken ct);

    /// <summary>
    /// Returns the sandbox with this id. When the runtime knows there is none, a handle whose
    /// <see cref="SandboxHandle.State"/> is <see cref="SandboxState.Missing"/>, carrying the run id the sandbox id names.
    /// Null only when the runtime cannot tell: its engine failed or did not answer, or the id is not one of its sandboxes.
    /// </summary>
    ValueTask<SandboxHandle?> GetAsync(string sandboxId, CancellationToken ct);

    /// <summary>Every sandbox this runtime created that still exists, found by label, including ones from before a host restart.</summary>
    ValueTask<IReadOnlyList<SandboxHandle>> ListAsync(CancellationToken ct);

    /// <summary>
    /// Starts an exited sandbox of this runtime again, with the volume it had. A running one succeeds at once. Fails,
    /// never throws, when the sandbox is absent, not this runtime's, or the engine fails.
    /// </summary>
    ValueTask<UnitResult<AgentError>> StartAsync(string sandboxId, CancellationToken ct);

    /// <summary>Stops and deletes the sandbox and its volume. An absent sandbox succeeds.</summary>
    ValueTask<UnitResult<AgentError>> DeleteAsync(string sandboxId, CancellationToken ct);
}
