namespace Thalos;

/// <summary>Host-configurable ceilings for detached runs. Bound from the "Thalos" configuration section.</summary>
public sealed class SubagentOptions
{
    /// <summary>
    /// Maximum <see cref="SubagentRunRequest.Depth"/> accepted. Default 2. Nothing increments depth today — this is
    /// the guard that stops a future delegation tool recursing without bound.
    /// </summary>
    public int MaxDepth { get; set; } = 2;

    /// <summary>
    /// Budget the runner applies when a <see cref="SubagentRunRequest"/> leaves <see cref="SubagentRunRequest.Budget"/>
    /// <see langword="null"/>. A request that names its own budget always wins over this value — this is the fallback,
    /// never an override.
    /// </summary>
    public SubagentBudget DefaultBudget { get; set; } = SubagentBudget.Default;
}
