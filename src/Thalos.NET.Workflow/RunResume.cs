namespace Thalos.Workflow;

/// <summary>
/// A single successful resume of a run parked at a gate: who resumed it, when, and which signal they satisfied.
/// </summary>
/// <param name="By">The principal who resumed the run.</param>
/// <param name="At">When the resume was recorded, on the store's clock.</param>
/// <param name="Signal">The signal the resume satisfied.</param>
public sealed record RunResume(RunPrincipal By, DateTimeOffset At, string Signal);
