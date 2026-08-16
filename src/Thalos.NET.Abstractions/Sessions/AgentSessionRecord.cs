namespace Thalos;

/// <summary>Persistent header of a session; messages are stored separately.</summary>
public sealed record AgentSessionRecord(
    SessionId Id,
    AgentId AgentId,
    string OwnerId,
    SessionState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    int TurnCount,
    long TotalInputTokens,
    long TotalOutputTokens);
