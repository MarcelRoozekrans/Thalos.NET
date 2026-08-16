namespace Thalos;

/// <summary>
/// Buffered outcome of one successful turn: the final assistant <paramref name="Text"/>, token <paramref name="Usage"/>
/// summed over all model round-trips, every tool call made in order, and wall-clock <paramref name="Elapsed"/>.
/// </summary>
public sealed record AgentTurnResult(
    TurnId TurnId,
    SessionId SessionId,
    string Text,
    TurnUsage Usage,
    IReadOnlyList<ToolCallSummary> ToolCalls,
    TimeSpan Elapsed);
