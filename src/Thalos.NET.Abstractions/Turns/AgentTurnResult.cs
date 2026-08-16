namespace Thalos;

public sealed record AgentTurnResult(
    TurnId TurnId,
    SessionId SessionId,
    string Text,
    TurnUsage Usage,
    IReadOnlyList<ToolCallSummary> ToolCalls,
    TimeSpan Elapsed);
