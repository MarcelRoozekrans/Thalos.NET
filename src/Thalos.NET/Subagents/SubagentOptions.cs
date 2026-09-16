namespace Thalos;

/// <summary>Host-configurable ceilings for detached runs. Bound from the "Thalos" configuration section.</summary>
public sealed class SubagentOptions
{
    /// <summary>
    /// Maximum <see cref="SubagentRunRequest.Depth"/> accepted. Default 2. Nothing increments depth today — this is
    /// the guard that stops a future delegation tool recursing without bound.
    /// </summary>
    public int MaxDepth { get; set; } = 2;

    /// <summary>Budget applied when a request does not carry one.</summary>
    public SubagentBudget DefaultBudget { get; set; } = SubagentBudget.Default;
}
