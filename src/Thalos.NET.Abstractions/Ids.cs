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
