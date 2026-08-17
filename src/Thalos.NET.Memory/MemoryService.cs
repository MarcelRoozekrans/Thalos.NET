using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Thalos.Runtime;
using ZeroAlloc.Authorization;
using ZeroAlloc.Inject;
using ZeroAlloc.Results;

namespace Thalos.Memory;

/// <inheritdoc cref="IMemoryService" />
[Singleton(As = typeof(IMemoryService))]
public sealed partial class MemoryService(
    IMemoryStore store,
    IMemoryIndex index,
    IOptions<MemoryOptions> options,
    TimeProvider clock,
    AgentEventHub hub,
    ILogger<MemoryService>? logger = null) : IMemoryService
{
    private readonly ILogger _logger = logger ?? NullLogger<MemoryService>.Instance;

    /// <inheritdoc />
    public async ValueTask<Result<MemoryRecord, AgentError>> RememberAsync(RememberRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.OwnerId) || string.Equals(request.OwnerId, AnonymousSecurityContext.AnonymousId, StringComparison.Ordinal))
        {
            return Result<MemoryRecord, AgentError>.Failure(AgentError.MemoryValidationFailed("OwnerId must be a non-blank, non-anonymous id."));
        }

        var now = clock.GetUtcNow();
        var record = new MemoryRecord
        {
            Id = MemoryId.New(),
            OwnerId = request.OwnerId,
            AgentId = request.AgentId,
            Kind = request.Kind,
            Text = request.Text?.Trim() ?? "",
            Tags = MemoryRules.NormalizeTags(request.Tags),
            Source = request.Source,
            Importance = request.Importance,
            CreatedAt = now,
            UpdatedAt = now,
            IndexPending = true, // cleared after a successful upsert — a crash in between leaves it pending (repaired by reindex)
        };
        if (MemoryRules.Validate(record) is { } invalid)
        {
            return Result<MemoryRecord, AgentError>.Failure(invalid);
        }

        var opts = options.Value;
        if (opts.Dedupe.Enabled && await FindDuplicateAsync(record, opts.Dedupe.Threshold, ct).ConfigureAwait(false) is { } duplicate)
        {
            var refreshed = await store.UpdateAsync(duplicate.Id, new MemoryUpdate { Importance = Math.Max(duplicate.Importance, record.Importance) }, ct).ConfigureAwait(false);
            if (refreshed.IsSuccess)
            {
                await MemoryEvents.PublishAsync(hub, (s, t) => new MemoryStoredEvent(s, t, refreshed.Value.Id, refreshed.Value.Kind.Value, Deduped: true), ct).ConfigureAwait(false);
                return refreshed;
            }

            LogDedupeRefreshFailed(_logger, duplicate.Id, refreshed.Error.ToString()); // fall through and insert
        }

        var created = await store.CreateAsync(record, ct).ConfigureAwait(false);
        if (created.IsFailure)
        {
            return created;
        }

        var indexed = await index.UpsertAsync([created.Value], ct).ConfigureAwait(false);
        if (indexed.IsFailure)
        {
            LogIndexPending(_logger, record.Id, indexed.Error.ToString());
            await MemoryEvents.PublishAsync(hub, (s, t) => new MemoryIndexPendingEvent(s, t, record.Id), ct).ConfigureAwait(false);
            return created;
        }

        var cleared = await store.UpdateAsync(record.Id, new MemoryUpdate { IndexPending = false }, ct).ConfigureAwait(false);
        var final = cleared.IsSuccess ? cleared.Value : created.Value;
        await MemoryEvents.PublishAsync(hub, (s, t) => new MemoryStoredEvent(s, t, final.Id, final.Kind.Value, Deduped: false), ct).ConfigureAwait(false);
        return Result<MemoryRecord, AgentError>.Success(final);
    }

    /// <summary>Same owner, same agent scope (no shared owner), score ≥ threshold, not archived. An index failure means "no duplicate" (remember still stores).</summary>
    private async ValueTask<MemoryRecord?> FindDuplicateAsync(MemoryRecord candidate, double threshold, CancellationToken ct)
    {
        var scope = new MemoryScope(candidate.OwnerId, candidate.AgentId, SharedOwnerId: null);
        var hits = await index.SearchAsync(candidate.Text, scope, new MemorySearchOptions(TopK: 1, MinScore: threshold), ct).ConfigureAwait(false);
        if (hits.IsFailure || hits.Value.Count == 0)
        {
            return null;
        }

        var existing = await store.GetAsync(hits.Value[0].Id, ct).ConfigureAwait(false);
        return existing.IsSuccess && !existing.Value.IsArchived && string.Equals(existing.Value.OwnerId, candidate.OwnerId, StringComparison.Ordinal)
            ? existing.Value
            : null;
    }

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<RecalledMemory>, AgentError>> RecallAsync(string query, MemoryScope scope, RecallOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrEmpty(scope.OwnerId))
        {
            return Result<IReadOnlyList<RecalledMemory>, AgentError>.Success([]);
        }

        var topK = Math.Max(1, options.TopK); // options is a shared bound instance — read, never mutate
        var hits = await index.SearchAsync(query, scope, new MemorySearchOptions(topK * 2, options.MinScore), ct).ConfigureAwait(false); // over-fetch: archived/stale hits are dropped below
        if (hits.IsFailure)
        {
            return Result<IReadOnlyList<RecalledMemory>, AgentError>.Failure(hits.Error);
        }

        var hydrated = await HydrateAsync(hits.Value, scope, ct).ConfigureAwait(false);
        if (hydrated.IsFailure)
        {
            return Result<IReadOnlyList<RecalledMemory>, AgentError>.Failure(hydrated.Error);
        }

        var candidates = hydrated.Value;
        candidates.Sort(CompareCandidates);
        var selected = SelectWithinBudget(candidates, topK, options.MaxChars);
        if (selected.Count > 0)
        {
            var marked = await store.MarkRecalledAsync(selected.Select(s => s.Record.Id).ToList(), clock.GetUtcNow(), ct).ConfigureAwait(false);
            if (marked.IsFailure)
            {
                LogMarkRecalledFailed(_logger, marked.Error.ToString());
            }
        }

        return Result<IReadOnlyList<RecalledMemory>, AgentError>.Success(selected);
    }

    /// <summary>Loads each hit from the store; drops stale (not found), archived and out-of-scope records; any other store failure is returned.</summary>
    private async ValueTask<Result<List<RecalledMemory>, AgentError>> HydrateAsync(IReadOnlyList<MemoryHit> hits, MemoryScope scope, CancellationToken ct)
    {
        var candidates = new List<RecalledMemory>(hits.Count);
        foreach (var hit in hits)
        {
            var got = await store.GetAsync(hit.Id, ct).ConfigureAwait(false);
            if (got.IsFailure)
            {
                if (got.Error.Code == AgentErrorCode.MemoryNotFound)
                {
                    continue; // stale index entry — harmless
                }

                return Result<List<RecalledMemory>, AgentError>.Failure(got.Error);
            }

            var record = got.Value;
            if (!record.IsArchived && scope.Includes(record.OwnerId, record.AgentId))
            {
                candidates.Add(new RecalledMemory(record, hit.Score));
            }
        }

        return Result<List<RecalledMemory>, AgentError>.Success(candidates);
    }

    /// <summary>Score desc, then importance desc, then <c>UpdatedAt</c> desc.</summary>
    private static int CompareCandidates(RecalledMemory a, RecalledMemory b)
    {
        var c = b.Score.CompareTo(a.Score);
        if (c == 0)
        {
            c = b.Record.Importance.CompareTo(a.Record.Importance);
        }

        if (c == 0)
        {
            c = b.Record.UpdatedAt.CompareTo(a.Record.UpdatedAt);
        }

        return c;
    }

    /// <summary>Takes ordered candidates while fewer than <paramref name="topK"/> are selected; one whose text does not fit the remaining <paramref name="maxChars"/> is skipped (a smaller later one may still fit).</summary>
    private static List<RecalledMemory> SelectWithinBudget(List<RecalledMemory> candidates, int topK, int maxChars)
    {
        var selected = new List<RecalledMemory>(Math.Min(topK, candidates.Count));
        var chars = 0;
        foreach (var candidate in candidates)
        {
            if (selected.Count >= topK)
            {
                break;
            }

            if (chars + candidate.Record.Text.Length > maxChars)
            {
                continue; // does not fit the budget; a smaller later candidate may
            }

            chars += candidate.Record.Text.Length;
            selected.Add(candidate);
        }

        return selected;
    }

    // Task 12 implements these (MA0025 forbids NotImplementedException; these bodies fail every test until then).
    public ValueTask<UnitResult<AgentError>> ForgetAsync(MemoryId id, MemoryScope scope, bool hard, CancellationToken ct) =>
        new(UnitResult<AgentError>.Failure(AgentError.MemoryValidationFailed("not implemented")));

    public ValueTask<Result<MemoryPage, AgentError>> ListAsync(MemoryQuery query, CancellationToken ct) =>
        new(Result<MemoryPage, AgentError>.Failure(AgentError.MemoryValidationFailed("not implemented")));

    public ValueTask<Result<ReindexReport, AgentError>> ReindexAsync(ReindexOptions options, CancellationToken ct) =>
        new(Result<ReindexReport, AgentError>.Failure(AgentError.MemoryValidationFailed("not implemented")));

    [LoggerMessage(EventId = 500, Level = LogLevel.Warning, Message = "Memory {Memory} stored but not indexed (pending): {Error}")]
    private static partial void LogIndexPending(ILogger logger, MemoryId memory, string error);

    [LoggerMessage(EventId = 501, Level = LogLevel.Warning, Message = "Refreshing duplicate memory {Memory} failed, inserting instead: {Error}")]
    private static partial void LogDedupeRefreshFailed(ILogger logger, MemoryId memory, string error);

    [LoggerMessage(EventId = 502, Level = LogLevel.Warning, Message = "MarkRecalled failed (recall still returned): {Error}")]
    private static partial void LogMarkRecalledFailed(ILogger logger, string error);
}
