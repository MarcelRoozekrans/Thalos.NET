using System.Runtime.InteropServices;

namespace Thalos;

/// <summary>
/// Ceilings for one detached run. Both are hard stops, not hints: a detached run has no human watching it, so an
/// unbounded loop costs real money with nobody to notice.
/// </summary>
/// <param name="MaxTotalTokens">Total tokens across every model round-trip of the run. Exceeding it fails the run.</param>
/// <param name="Deadline">Wall-clock ceiling for the whole run, measured from the moment the session is created.</param>
[StructLayout(LayoutKind.Auto)] // MA0008: all-blittable fields — make the layout choice explicit
public readonly record struct SubagentBudget(int MaxTotalTokens, TimeSpan Deadline)
{
    /// <summary>50,000 tokens and 10 minutes — enough for a multi-step research turn, small enough to notice.</summary>
    public static SubagentBudget Default => new(50_000, TimeSpan.FromMinutes(10));
}
