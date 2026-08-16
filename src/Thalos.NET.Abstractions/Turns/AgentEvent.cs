namespace Thalos;

/// <summary>Streaming event emitted while a turn runs. <see cref="Kind"/> is the stable wire name (SSE event type).</summary>
public abstract record AgentEvent(SessionId SessionId, TurnId TurnId)
{
    public abstract string Kind { get; }

    public static string KindOf(Type eventType) => eventType.Name switch
    {
        nameof(TextDeltaEvent) => "text-delta",
        nameof(ToolCallStartedEvent) => "tool-call",
        nameof(ToolCallFinishedEvent) => "tool-result",
        nameof(UsageEvent) => "usage",
        nameof(TurnCompletedEvent) => "done",
        nameof(TurnFailedEvent) => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(eventType), eventType, "Unknown AgentEvent type"),
    };
}

public sealed record TextDeltaEvent(SessionId SessionId, TurnId TurnId, string Text) : AgentEvent(SessionId, TurnId)
{ public override string Kind => "text-delta"; }

public sealed record ToolCallStartedEvent(SessionId SessionId, TurnId TurnId, ToolCallId CallId, string ToolName, string ArgumentsJson) : AgentEvent(SessionId, TurnId)
{ public override string Kind => "tool-call"; }

public sealed record ToolCallFinishedEvent(SessionId SessionId, TurnId TurnId, ToolCallId CallId, string ToolName, bool Succeeded, string? ResultPreview, TimeSpan Elapsed) : AgentEvent(SessionId, TurnId)
{ public override string Kind => "tool-result"; }

public sealed record UsageEvent(SessionId SessionId, TurnId TurnId, TurnUsage Usage) : AgentEvent(SessionId, TurnId)
{ public override string Kind => "usage"; }

public sealed record TurnCompletedEvent(SessionId SessionId, TurnId TurnId, AgentTurnResult Result) : AgentEvent(SessionId, TurnId)
{ public override string Kind => "done"; }

public sealed record TurnFailedEvent(SessionId SessionId, TurnId TurnId, AgentError Error) : AgentEvent(SessionId, TurnId)
{ public override string Kind => "error"; }
