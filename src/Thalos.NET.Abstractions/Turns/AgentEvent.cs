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
    public const string MemoryRecalled = "memory-recalled";
    public const string MemoryStored = "memory-stored";
    public const string MemoryRecallFailed = "memory-recall-failed";
    public const string MemoryIndexPending = "memory-index-pending";
    public const string MemoryQuarantined = "memory-quarantined";
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

        if (eventType == typeof(MemoryRecalledEvent))
        {
            return AgentEventKinds.MemoryRecalled;
        }

        if (eventType == typeof(MemoryStoredEvent))
        {
            return AgentEventKinds.MemoryStored;
        }

        if (eventType == typeof(MemoryRecallFailedEvent))
        {
            return AgentEventKinds.MemoryRecallFailed;
        }

        if (eventType == typeof(MemoryIndexPendingEvent))
        {
            return AgentEventKinds.MemoryIndexPending;
        }

        if (eventType == typeof(MemoryQuarantinedEvent))
        {
            return AgentEventKinds.MemoryQuarantined;
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

/// <summary>Auto-recall injected <paramref name="MemoryIds"/> (in block order) into the turn; <paramref name="Chars"/> is the size of the injected block.</summary>
public sealed record MemoryRecalledEvent(SessionId SessionId, TurnId TurnId, IReadOnlyList<MemoryId> MemoryIds, int Chars) : AgentEvent(SessionId, TurnId)
{
    public override string Kind => AgentEventKinds.MemoryRecalled;

    /// <summary>Number of memories injected.</summary>
    public int Count => MemoryIds.Count;
}

/// <summary>A memory was stored (or, when <paramref name="Deduped"/>, an equivalent existing memory was refreshed instead). <paramref name="MemoryKind"/> is the kind value (e.g. <c>fact</c>).</summary>
public sealed record MemoryStoredEvent(SessionId SessionId, TurnId TurnId, MemoryId MemoryId, string MemoryKind, bool Deduped) : AgentEvent(SessionId, TurnId)
{ public override string Kind => AgentEventKinds.MemoryStored; }

/// <summary>Auto-recall failed with <paramref name="Code"/>; the turn continued without memories.</summary>
public sealed record MemoryRecallFailedEvent(SessionId SessionId, TurnId TurnId, AgentErrorCode Code) : AgentEvent(SessionId, TurnId)
{ public override string Kind => AgentEventKinds.MemoryRecallFailed; }

/// <summary>A memory was stored but could not be indexed; it stays <c>IndexPending</c> until a reindex succeeds.</summary>
public sealed record MemoryIndexPendingEvent(SessionId SessionId, TurnId TurnId, MemoryId MemoryId) : AgentEvent(SessionId, TurnId)
{ public override string Kind => AgentEventKinds.MemoryIndexPending; }

/// <summary>A recalled memory was dropped from the injected block because the untrusted-content scanner quarantined it; <paramref name="Detail"/> is e.g. <c>"High: SEC-01"</c>.</summary>
public sealed record MemoryQuarantinedEvent(SessionId SessionId, TurnId TurnId, MemoryId MemoryId, string? Detail) : AgentEvent(SessionId, TurnId)
{ public override string Kind => AgentEventKinds.MemoryQuarantined; }
