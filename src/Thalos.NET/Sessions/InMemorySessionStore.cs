using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using ZeroAlloc.Results;

namespace Thalos.Sessions;

/// <summary>Non-durable store for tests, samples and CLI hosts.</summary>
public sealed class InMemorySessionStore(TimeProvider clock) : IAgentSessionStore
{
    private sealed class Entry
    {
        public required AgentSessionRecord Record;
        public readonly List<ChatMessage> Messages = [];
        public readonly object Gate = new();
    }

    private readonly ConcurrentDictionary<SessionId, Entry> _sessions = new();

    public ValueTask<Result<AgentSessionRecord, AgentError>> CreateAsync(AgentId agentId, string ownerId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var record = new AgentSessionRecord(SessionId.New(), agentId, ownerId, SessionState.Idle, now, now, 0, 0, 0);
        _sessions[record.Id] = new Entry { Record = record };
        return new(Result<AgentSessionRecord, AgentError>.Success(record));
    }

    public ValueTask<Result<AgentSessionRecord, AgentError>> GetAsync(SessionId id, CancellationToken ct) =>
        new(_sessions.TryGetValue(id, out var e)
            ? Result<AgentSessionRecord, AgentError>.Success(e.Record)
            : Result<AgentSessionRecord, AgentError>.Failure(AgentError.SessionNotFound(id)));

    public ValueTask<Result<IReadOnlyList<AgentSessionRecord>, AgentError>> ListAsync(string ownerId, int skip, int take, CancellationToken ct)
    {
        IReadOnlyList<AgentSessionRecord> page = _sessions.Values
            .Select(e => e.Record)
            .Where(r => string.Equals(r.OwnerId, ownerId, StringComparison.Ordinal))
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Skip(skip).Take(take)
            .ToList();
        return new(Result<IReadOnlyList<AgentSessionRecord>, AgentError>.Success(page));
    }

    public ValueTask<Result<IReadOnlyList<ChatMessage>, AgentError>> LoadMessagesAsync(SessionId id, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(id, out var e))
        {
            return new(Result<IReadOnlyList<ChatMessage>, AgentError>.Failure(AgentError.SessionNotFound(id)));
        }

        lock (e.Gate)
        {
            // snapshot under the gate — callers must never observe later appends through this list
            IReadOnlyList<ChatMessage> copy = e.Messages.ToArray();
            return new(Result<IReadOnlyList<ChatMessage>, AgentError>.Success(copy));
        }
    }

    public ValueTask<UnitResult<AgentError>> AppendMessagesAsync(SessionId id, IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(id, out var e))
        {
            return new(UnitResult<AgentError>.Failure(AgentError.SessionNotFound(id)));
        }

        lock (e.Gate)
        {
            e.Messages.AddRange(messages);
        }

        return new(UnitResult<AgentError>.Success());
    }

    public ValueTask<UnitResult<AgentError>> RecordTurnAsync(SessionId id, TurnUsage usage, CancellationToken ct) =>
        Mutate(id, r => r with
        {
            TurnCount = r.TurnCount + 1,
            TotalInputTokens = r.TotalInputTokens + usage.InputTokens,
            TotalOutputTokens = r.TotalOutputTokens + usage.OutputTokens,
            LastActivityAt = clock.GetUtcNow(),
        });

    public ValueTask<UnitResult<AgentError>> UpdateStateAsync(SessionId id, SessionState state, CancellationToken ct) =>
        Mutate(id, r => r with { State = state, LastActivityAt = clock.GetUtcNow() });

    private ValueTask<UnitResult<AgentError>> Mutate(SessionId id, Func<AgentSessionRecord, AgentSessionRecord> update)
    {
        if (!_sessions.TryGetValue(id, out var e))
        {
            return new(UnitResult<AgentError>.Failure(AgentError.SessionNotFound(id)));
        }

        lock (e.Gate)
        {
            e.Record = update(e.Record);
        }

        return new(UnitResult<AgentError>.Success());
    }
}
