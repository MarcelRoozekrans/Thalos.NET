namespace Thalos.Mcp;

/// <summary>How long <see cref="RemoteRunToolSource"/> waits on a run's remote tool endpoint.</summary>
public sealed class RemoteRunToolOptions
{
    /// <summary>
    /// How long resolving a run's endpoint and connecting to it may take, before the call is answered with an
    /// <c>error:</c> result. Default 30 seconds. Must be positive and at most <see cref="int.MaxValue"/> milliseconds.
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long one call may run on the run's endpoint before it is cancelled and answered with an <c>error:</c>
    /// result. A <c>sandbox__test</c> call can be long; this bounds one call. Default 20 minutes. Must be positive and at
    /// most <see cref="int.MaxValue"/> milliseconds. It applies to the sources Thalos.NET.Sandbox's
    /// <c>AddRemoteRunTools</c> adds; a remote MCP entry is bounded by its own
    /// <see cref="RunScopedMcpDefinition.CallTimeout"/> instead.
    /// </summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>
    /// How long a call waits for each <see cref="Thalos.Workspaces.IRunToolCallObserver"/> before it logs the observer as
    /// late and goes on; the observer's token is cancelled then and it is left to finish on its own. Default 10 seconds.
    /// Must be positive and at most <see cref="int.MaxValue"/> milliseconds.
    /// </summary>
    public TimeSpan ObserverTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
