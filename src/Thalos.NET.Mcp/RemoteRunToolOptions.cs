namespace Thalos.Mcp;

/// <summary>How long <see cref="RemoteRunToolSource"/> waits on a run's remote tool endpoint, and how much it reads from it.</summary>
public sealed class RemoteRunToolOptions
{
    /// <summary>
    /// How long resolving a run's endpoint and connecting to it may take, before the call is answered with an
    /// <c>error:</c> result. Default 30 seconds. Must be positive and at most <see cref="int.MaxValue"/> milliseconds.
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long one call may run on the run's endpoint before it is cancelled and answered with an <c>error:</c>
    /// result. Default 30 minutes. Must be positive and at most <see cref="int.MaxValue"/> milliseconds. It applies to the
    /// sources Thalos.NET.Sandbox's <c>AddRemoteRunTools</c> adds; a remote MCP entry is bounded by its own
    /// <see cref="RunScopedMcpDefinition.CallTimeout"/> instead.
    /// </summary>
    /// <remarks>
    /// One <c>sandbox__test</c> call can spend two bounded stretches in the sandbox, one after the other: a restore after
    /// a build-file change, bounded by the sandbox host at 10 minutes, then copying the workspace and running the tests,
    /// which share Thalos.NET.Sandbox's <c>SandboxToolOptions.TestTimeout</c>, 15 minutes by default. Keep this above
    /// their sum, so the sandbox's own bounds, which say what timed out, end a slow call before this one cuts it off; raise
    /// it with <c>TestTimeout</c>.
    /// </remarks>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a call waits for each <see cref="Thalos.Workspaces.IRunToolCallObserver"/> before it logs the observer as
    /// late and goes on; the observer's token is cancelled then and it is left to finish on its own. Default 10 seconds.
    /// Must be positive and at most <see cref="int.MaxValue"/> milliseconds.
    /// </summary>
    public TimeSpan ObserverTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many bytes one response from a run's endpoint may hold, such as one call's answer, before the call is answered
    /// with an <c>error:</c> result and the run's client is dropped. The endpoint runs agent-controlled code, and the MCP
    /// client reads a whole answer into memory, so this bounds what one call may make the host hold. Default 4 MiB.
    /// Must be positive.
    /// </summary>
    public long MaxResultBytes { get; set; } = 4 * 1024 * 1024;
}
