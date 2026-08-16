namespace Thalos;

/// <summary>Stable wire names (SSE event types) for <see cref="AgentEvent"/> subclasses.</summary>
public static class AgentEventKinds
{
    public const string TextDelta = "text-delta";
    public const string ToolCall = "tool-call";
    public const string ToolResult = "tool-result";
    public const string Usage = "usage";
    public const string Done = "done";
    public const string Error = "error";
}

/// <summary>Streaming event emitted while a turn runs. <see cref="Kind"/> is the stable wire name (SSE event type), see <see cref="AgentEventKinds"/>.</summary>
public abstract record AgentEvent(SessionId SessionId, TurnId TurnId)
{
    public abstract string Kind { get; }

    /// <summary>Maps a concrete event type to its wire name; throws <see cref="ArgumentOutOfRangeException"/> for any other type.</summary>
    public static string KindOf(Type eventType)
    {
        if (eventType == typeof(TextDeltaEvent))
        {
            return AgentEventKinds.TextDelta;
        }

        if (eventType == typeof(ToolCallStartedEvent))
        {
            return AgentEventKinds.ToolCall;
        }

        if (eventType == typeof(ToolCallFinishedEvent))
        {
            return AgentEventKinds.ToolResult;
        }

        if (eventType == typeof(UsageEvent))
        {
            return AgentEventKinds.Usage;
        }

        if (eventType == typeof(TurnCompletedEvent))
        {
            return AgentEventKinds.Done;
        }

        if (eventType == typeof(TurnFailedEvent))
        {
            return AgentEventKinds.Error;
        }

        throw new ArgumentOutOfRangeException(nameof(eventType), eventType, "Unknown AgentEvent type");
    }
}

/// <summary>A chunk of assistant text.</summary>
public sealed record TextDeltaEvent(SessionId SessionId, TurnId TurnId, string Text) : AgentEvent(SessionId, TurnId)
{ public override string Kind => AgentEventKinds.TextDelta; }

/// <summary>The model requested a tool call (emitted before the tool runs).</summary>
public sealed record ToolCallStartedEvent(SessionId SessionId, TurnId TurnId, ToolCallId CallId, string ToolName, string ArgumentsJson) : AgentEvent(SessionId, TurnId)
{ public override string Kind => AgentEventKinds.ToolCall; }

/// <summary>A tool call finished (successfully or not).</summary>
public sealed record ToolCallFinishedEvent(SessionId SessionId, TurnId TurnId, ToolCallId CallId, string ToolName, bool Succeeded, string? ResultPreview, TimeSpan Elapsed) : AgentEvent(SessionId, TurnId)
{ public override string Kind => AgentEventKinds.ToolResult; }

/// <summary>Token usage of the turn: one summed usage event per turn (all model round-trips), emitted just before <see cref="TurnCompletedEvent"/>.</summary>
public sealed record UsageEvent(SessionId SessionId, TurnId TurnId, TurnUsage Usage) : AgentEvent(SessionId, TurnId)
{ public override string Kind => AgentEventKinds.Usage; }

/// <summary>Terminal event of a successful turn; carries the buffered result.</summary>
public sealed record TurnCompletedEvent(SessionId SessionId, TurnId TurnId, AgentTurnResult Result) : AgentEvent(SessionId, TurnId)
{ public override string Kind => AgentEventKinds.Done; }

/// <summary>
/// Terminal event of a failed turn. <paramref name="Usage"/> is the token usage of the model round-trips that completed before the
/// failure (zero for pre-claim failures), so hosts can bill failed/quarantined turns; no <see cref="UsageEvent"/> is emitted for a failed turn.
/// </summary>
public sealed record TurnFailedEvent(SessionId SessionId, TurnId TurnId, AgentError Error, TurnUsage Usage = default) : AgentEvent(SessionId, TurnId)
{ public override string Kind => AgentEventKinds.Error; }
