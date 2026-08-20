using ZeroAlloc.ValueObjects;

namespace Thalos;

/// <summary>Identifies an agent definition.</summary>
[TypedId]
public readonly partial record struct AgentId;

/// <summary>Identifies a conversation session.</summary>
[TypedId]
public readonly partial record struct SessionId;

/// <summary>Identifies one turn (user message → agent reply) inside a session.</summary>
[TypedId]
public readonly partial record struct TurnId;

/// <summary>Identifies one tool invocation inside a turn.</summary>
[TypedId]
public readonly partial record struct ToolCallId;

/// <summary>Identifies one memory record (Thalos.NET.Memory).</summary>
[TypedId]
public readonly partial record struct MemoryId;

/// <summary>
/// Identifies one external conversation within a channel (a Telegram chat id, a console session). Opaque to Thalos.
/// </summary>
/// <remarks>
/// Hand-written rather than <c>[TypedId]</c>: ZeroAlloc.ValueObjects 2.0.5's <c>TypedIdAttribute</c> only source-generates
/// Guid- or long-backed identifiers (<c>Ulid</c>, <c>Uuid7</c>, <c>Snowflake</c>, <c>Sequential</c>) — there is no
/// string-backed strategy, so this id is defined by hand instead, matching the shape (a <see cref="Value"/> property
/// and a <see cref="ToString"/> that round-trips it) as closely as a plain <c>record struct</c> allows.
/// </remarks>
/// <param name="Value">The channel-supplied conversation identifier, taken as-is.</param>
public readonly record struct ConversationId(string Value)
{
    /// <summary>Returns the wrapped conversation identifier.</summary>
    public override string ToString() => Value;
}
