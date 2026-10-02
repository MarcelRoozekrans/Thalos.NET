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
    /// most <see cref="int.MaxValue"/> milliseconds.
    /// </summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromMinutes(20);
}
