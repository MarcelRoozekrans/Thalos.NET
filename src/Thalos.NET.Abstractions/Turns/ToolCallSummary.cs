namespace Thalos;

/// <summary>What the model asked a tool to do and what came back — trimmed for display and audit.</summary>
public sealed record ToolCallSummary(
    ToolCallId Id,
    string ToolName,
    string ArgumentsJson,
    bool Succeeded,
    string? ResultPreview,
    TimeSpan Elapsed);
