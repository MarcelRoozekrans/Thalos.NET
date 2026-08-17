using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ZeroAlloc.Results;

namespace Thalos.Memory;

/// <summary>Non-durable store for tests, samples and CLI hosts.</summary>
public sealed class InMemoryMemoryStore(TimeProvider clock) : IMemoryStore
{
    private readonly ConcurrentDictionary<MemoryId, MemoryRecord> _records = new();
    private readonly object _gate = new(); // read-modify-write updates serialize here; fine for a test store

    /// <inheritdoc />
    public ValueTask<Result<MemoryRecord, AgentError>> CreateAsync(MemoryRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        var stored = record with { Tags = MemoryRules.NormalizeTags(record.Tags) };
        return new(_records.TryAdd(stored.Id, stored)
            ? Result<MemoryRecord, AgentError>.Success(stored)
            : Result<MemoryRecord, AgentError>.Failure(AgentError.MemoryStoreFailed("Duplicate memory id.")));
    }

    /// <inheritdoc />
    public ValueTask<Result<MemoryRecord, AgentError>> GetAsync(MemoryId id, CancellationToken ct) =>
        new(_records.TryGetValue(id, out var r) ? Result<MemoryRecord, AgentError>.Success(r) : Result<MemoryRecord, AgentError>.Failure(AgentError.MemoryNotFound(id)));

    /// <inheritdoc />
    public ValueTask<Result<MemoryRecord, AgentError>> UpdateAsync(MemoryId id, MemoryUpdate update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var existing))
            {
                return new(Result<MemoryRecord, AgentError>.Failure(AgentError.MemoryNotFound(id)));
            }

            var updated = existing with
            {
                Text = update.Text ?? existing.Text,
                Tags = update.Tags is null ? existing.Tags : MemoryRules.NormalizeTags(update.Tags),
                Importance = update.Importance ?? existing.Importance,
                IsArchived = update.IsArchived ?? existing.IsArchived,
                IndexPending = update.IndexPending ?? existing.IndexPending,
                UpdatedAt = update.TouchesContent ? clock.GetUtcNow() : existing.UpdatedAt,
            };
            _records[id] = updated;
            return new(Result<MemoryRecord, AgentError>.Success(updated));
        }
    }

    /// <inheritdoc />
    public ValueTask<UnitResult<AgentError>> DeleteAsync(MemoryId id, CancellationToken ct)
    {
        lock (_gate) // under the gate so a concurrent Update/MarkRecalled cannot re-insert the record it read before the delete
        {
            return new(_records.TryRemove(id, out _) ? UnitResult<AgentError>.Success() : UnitResult<AgentError>.Failure(AgentError.MemoryNotFound(id)));
        }
    }

    /// <inheritdoc />
    public ValueTask<Result<MemoryPage, AgentError>> ListAsync(MemoryQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, MemoryQuery.MaxPageSize);
        var matches = _records.Values.Where(query.Matches).OrderByDescending(r => r.UpdatedAt).ThenByDescending(r => r.Id).ToList();
        var skip = (int)Math.Min((long)(page - 1) * size, matches.Count); // long arithmetic: a huge Page must not overflow into a negative skip
        IReadOnlyList<MemoryRecord> items = matches.Skip(skip).Take(size).ToList();
        return new(Result<MemoryPage, AgentError>.Success(new MemoryPage(items, page, size, matches.Count)));
    }

    /// <inheritdoc />
    public ValueTask<UnitResult<AgentError>> MarkRecalledAsync(IReadOnlyList<MemoryId> ids, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var seen = new HashSet<MemoryId>(); // ids are a set: a duplicate id counts once
        lock (_gate)
        {
            foreach (var id in ids)
            {
                if (seen.Add(id) && _records.TryGetValue(id, out var r))
                {
                    _records[id] = r with { RecallCount = r.RecallCount + 1, LastRecalledAt = at };
                }
            }
        }

        return new(UnitResult<AgentError>.Success());
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MemoryRecord> StreamAsync(MemoryQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var snapshot = _records.Values.Where(query.Matches).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToList();
        foreach (var r in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return r;
        }

        await Task.CompletedTask.ConfigureAwait(false); // keeps the iterator async without a real await (CS1998)
    }
}
