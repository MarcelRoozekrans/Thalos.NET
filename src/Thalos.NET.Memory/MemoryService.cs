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
    /// <summary>Score desc, importance desc, <c>UpdatedAt</c> desc, then id — a total order, so equal candidates always come back in the same sequence.</summary>
    private static readonly Comparison<RecalledMemory> CandidateOrder = CompareCandidates;

    private readonly ILogger _logger = logger ?? NullLogger<MemoryService>.Instance;
    private int _thresholdWarned;

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

        // Events after a successful write go out with CancellationToken.None: a late cancellation must not make the caller lose a committed record.
        var dedupe = options.Value.Dedupe;
        if (DedupeEnabled(dedupe) && await FindDuplicateAsync(record, dedupe.Threshold, ct).ConfigureAwait(false) is { } duplicate)
        {
            var refreshed = await store.UpdateAsync(duplicate.Id, new MemoryUpdate { Importance = Math.Max(duplicate.Importance, record.Importance) }, ct).ConfigureAwait(false);
            if (refreshed.IsSuccess)
            {
                await MemoryEvents.PublishAsync(hub, (s, t) => new MemoryStoredEvent(s, t, refreshed.Value.Id, refreshed.Value.Kind.Value, Deduped: true), CancellationToken.None).ConfigureAwait(false);
                return refreshed;
            }

            LogDedupeRefreshFailed(_logger, duplicate.Id, refreshed.Error.ToString()); // fall through and insert
        }

        var created = await store.CreateAsync(record, ct).ConfigureAwait(false);
        return created.IsFailure ? created : Result<MemoryRecord, AgentError>.Success(await IndexNewAsync(created.Value, ct).ConfigureAwait(false));
    }

    /// <summary>Upserts a freshly created record, clears <c>IndexPending</c> and publishes the matching event; never fails (the record is already committed).</summary>
    private async ValueTask<MemoryRecord> IndexNewAsync(MemoryRecord created, CancellationToken ct)
    {
        var indexed = await index.UpsertAsync([created], ct).ConfigureAwait(false);
        if (indexed.IsFailure)
        {
            LogIndexPending(_logger, created.Id, indexed.Error.ToString());
            await MemoryEvents.PublishAsync(hub, (s, t) => new MemoryIndexPendingEvent(s, t, created.Id), CancellationToken.None).ConfigureAwait(false);
            return created;
        }

        var cleared = await store.UpdateAsync(created.Id, new MemoryUpdate { IndexPending = false }, ct).ConfigureAwait(false);
        if (cleared.IsFailure)
        {
            LogClearPendingFailed(_logger, created.Id, cleared.Error.ToString()); // vector written; the flag stays set and the next reindex re-embeds it
        }

        var final = cleared.IsSuccess ? cleared.Value : created;
        await MemoryEvents.PublishAsync(hub, (s, t) => new MemoryStoredEvent(s, t, final.Id, final.Kind.Value, Deduped: false), CancellationToken.None).ConfigureAwait(false);
        return final;
    }

    /// <summary>Dedupe runs only when enabled and the threshold is a similarity in (0, 1]; a misconfigured threshold disables it (warned once).</summary>
    private bool DedupeEnabled(DedupeOptions dedupe)
    {
        if (!dedupe.Enabled)
        {
            return false;
        }

        if (!double.IsNaN(dedupe.Threshold) && dedupe.Threshold is > 0 and <= 1)
        {
            return true;
        }

        if (Interlocked.Exchange(ref _thresholdWarned, 1) == 0)
        {
            LogDedupeThresholdInvalid(_logger, dedupe.Threshold);
        }

        return false;
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
    public async ValueTask<Result<MemoryRecallResult, AgentError>> RecallAsync(string query, MemoryScope scope, RecallOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrEmpty(scope.OwnerId))
        {
            return Result<MemoryRecallResult, AgentError>.Success(new MemoryRecallResult([], MemoryRecallTier.None));
        }

        var topK = Math.Max(1, options.TopK); // options is a shared bound instance — read, never mutate
        var maxChars = options.MaxChars > 0 ? options.MaxChars : int.MaxValue; // MaxChars <= 0 = no char budget
        var semantic = await SemanticRecallAsync(query, scope, topK, options.MinScore, maxChars, ct).ConfigureAwait(false);
        if (semantic.IsFailure)
        {
            return Result<MemoryRecallResult, AgentError>.Failure(semantic.Error);
        }

        List<RecalledMemory> selected;
        MemoryRecallTier tier;
        if (semantic.Value.RawHitCount > 0)
        {
            // The index answered (even if every hit was archived/out of scope by the time it was hydrated): that is a
            // genuine "nothing relevant", not the index being down or empty-handed, so the tier stays Semantic.
            selected = semantic.Value.Selected;
            tier = MemoryRecallTier.Semantic;
        }
        else
        {
            // Index unavailable or raw search came back with zero hits: per IMemoryIndex's contract, on failure none of
            // the hits are valid, so this falls through to the store rather than returning a partial/failed recall.
            var recency = await RecencyFallbackAsync(scope, topK, maxChars, ct).ConfigureAwait(false);
            if (recency.IsFailure)
            {
                return Result<MemoryRecallResult, AgentError>.Failure(recency.Error);
            }

            selected = recency.Value;
            tier = selected.Count > 0 ? MemoryRecallTier.Recency : MemoryRecallTier.None;
            LogRecallDegraded(_logger, scope.OwnerId, tier);
        }

        if (selected.Count > 0)
        {
            var marked = await store.MarkRecalledAsync(selected.Select(s => s.Record.Id).ToList(), clock.GetUtcNow(), ct).ConfigureAwait(false);
            if (marked.IsFailure)
            {
                LogMarkRecalledFailed(_logger, marked.Error.ToString());
            }
        }

        return Result<MemoryRecallResult, AgentError>.Success(new MemoryRecallResult(selected, tier));
    }

    /// <summary>The <see cref="MemoryRecallTier.Semantic"/> attempt: hydrated, ordered, budgeted hits, plus the raw (pre-hydration) hit count the caller uses to decide whether to fall back.</summary>
    private readonly record struct SemanticAttempt(List<RecalledMemory> Selected, int RawHitCount);

    /// <summary>
    /// Searches the index and, only when it actually returned hits, hydrates/orders/budgets them. An index failure or a
    /// zero-hit search reports <c>RawHitCount == 0</c> and an empty selection without failing — the caller decides whether
    /// that means "fall back to recency".
    /// </summary>
    private async ValueTask<Result<SemanticAttempt, AgentError>> SemanticRecallAsync(string query, MemoryScope scope, int topK, double minScore, int maxChars, CancellationToken ct)
    {
        var fetch = (int)Math.Min(int.MaxValue, 2L * topK); // over-fetch: archived/stale hits are dropped below
        var hits = await index.SearchAsync(query, scope, new MemorySearchOptions(fetch, minScore), ct).ConfigureAwait(false);
        if (hits.IsFailure || hits.Value.Count == 0)
        {
            return Result<SemanticAttempt, AgentError>.Success(new SemanticAttempt([], 0));
        }

        var hydrated = await HydrateAsync(hits.Value, scope, ct).ConfigureAwait(false);
        if (hydrated.IsFailure)
        {
            return Result<SemanticAttempt, AgentError>.Failure(hydrated.Error);
        }

        var candidates = hydrated.Value;
        candidates.Sort(CandidateOrder);
        return Result<SemanticAttempt, AgentError>.Success(new SemanticAttempt(SelectWithinBudget(candidates, topK, maxChars), hits.Value.Count));
    }

    /// <summary>
    /// The store-backed <see cref="MemoryRecallTier.Recency"/> path: one exact-filtered, capped query per
    /// <see cref="MemoryScope.Partitions"/> entry — same pattern as <c>RagNetMemoryIndex.SearchAsync</c> — so a page boundary can
    /// never discard an in-scope row behind a noisier partition (e.g. another agent pinned to the same owner). Each partition's
    /// query still passes through <see cref="MemoryScope.Includes"/> as a post-filter: the query bounds what is fetched, the
    /// filter bounds what is returned. Merged, ordered by <c>UpdatedAt</c> descending, then the same TopK/MaxChars budget the
    /// semantic path applies. Score is reported as 0 (no similarity was computed — nothing here matched the query text).
    /// </summary>
    private async ValueTask<Result<List<RecalledMemory>, AgentError>> RecencyFallbackAsync(MemoryScope scope, int topK, int maxChars, CancellationToken ct)
    {
        var fetchPerPartition = (int)Math.Min(MemoryQuery.MaxPageSize, Math.Max(4L * topK, 20));
        var candidates = new List<RecalledMemory>();
        foreach (var (owner, agent) in scope.Partitions())
        {
            var query = new MemoryQuery
            {
                OwnerIds = [owner],
                AgentId = agent,
                OwnerWideOnly = agent is null,
                IncludeArchived = false,
                Page = 1,
                PageSize = fetchPerPartition,
            };
            var page = await store.ListAsync(query, ct).ConfigureAwait(false);
            if (page.IsFailure)
            {
                return Result<List<RecalledMemory>, AgentError>.Failure(page.Error);
            }

            foreach (var record in page.Value.Items)
            {
                if (scope.Includes(record.OwnerId, record.AgentId))
                {
                    candidates.Add(new RecalledMemory(record, 0));
                }
            }
        }

        candidates.Sort(static (a, b) =>
        {
            var byUpdatedAt = b.Record.UpdatedAt.CompareTo(a.Record.UpdatedAt);
            return byUpdatedAt != 0 ? byUpdatedAt : a.Record.Id.CompareTo(b.Record.Id); // deterministic ties
        });

        return Result<List<RecalledMemory>, AgentError>.Success(SelectWithinBudget(candidates, topK, maxChars));
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

    /// <summary>Score desc, then importance desc, then <c>UpdatedAt</c> desc, then id (deterministic ties).</summary>
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

        if (c == 0)
        {
            c = a.Record.Id.CompareTo(b.Record.Id);
        }

        return c;
    }

    /// <summary>Takes ordered candidates while fewer than <paramref name="topK"/> are selected; one whose text does not fit the remaining <paramref name="maxChars"/> is skipped (a smaller later one may still fit).</summary>
    private static List<RecalledMemory> SelectWithinBudget(List<RecalledMemory> candidates, int topK, int maxChars)
    {
        var selected = new List<RecalledMemory>(Math.Min(topK, candidates.Count));
        var chars = 0L;
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

    /// <inheritdoc />
    public async ValueTask<UnitResult<AgentError>> ForgetAsync(MemoryId id, MemoryScope scope, bool hard, CancellationToken ct)
    {
        var got = await store.GetAsync(id, ct).ConfigureAwait(false);
        if (got.IsFailure)
        {
            return UnitResult<AgentError>.Failure(got.Error);
        }

        if (!string.Equals(got.Value.OwnerId, scope.OwnerId, StringComparison.Ordinal))
        {
            return UnitResult<AgentError>.Failure(AgentError.MemoryForbidden(id)); // the shared owner grants read, never forget
        }

        if (hard)
        {
            var deleted = await store.DeleteAsync(id, ct).ConfigureAwait(false);
            if (deleted.IsFailure)
            {
                return deleted;
            }
        }
        else
        {
            // IndexPending too: the vector is removed below, so an un-archived record is picked up again by a pending-only reindex
            var archived = await store.UpdateAsync(id, new MemoryUpdate { IsArchived = true, IndexPending = true }, ct).ConfigureAwait(false);
            if (archived.IsFailure)
            {
                return UnitResult<AgentError>.Failure(archived.Error);
            }
        }

        var removed = await index.RemoveAsync(id, ct).ConfigureAwait(false);
        if (removed.IsFailure)
        {
            LogIndexRemoveFailed(_logger, id, removed.Error.ToString()); // a stale vector is dropped at hydration
        }

        return UnitResult<AgentError>.Success();
    }

    /// <inheritdoc />
    public ValueTask<Result<MemoryPage, AgentError>> ListAsync(MemoryQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.OwnerIds is { Count: > 0 }
            ? store.ListAsync(query, ct)
            : new(Result<MemoryPage, AgentError>.Failure(AgentError.MemoryValidationFailed("At least one owner id is required.")));
    }

    /// <inheritdoc />
    public async ValueTask<Result<ReindexReport, AgentError>> ReindexAsync(ReindexOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var probe = await index.ProbeAsync(ct).ConfigureAwait(false);
        if (probe.IsFailure)
        {
            return Result<ReindexReport, AgentError>.Failure(probe.Error);
        }

        if (!probe.Value.Available)
        {
            return Result<ReindexReport, AgentError>.Failure(AgentError.MemoryIndexUnavailable("The memory index is unavailable.", probe.Value.Detail));
        }

        var query = new MemoryQuery { IndexPending = options.PendingOnly ? true : null, IncludeArchived = false };
        var batchSize = Math.Max(1, options.BatchSize);
        var scanned = 0;
        var indexed = 0;
        var failed = 0;
        var batch = new List<MemoryRecord>(batchSize);
        try
        {
            await foreach (var record in store.StreamAsync(query, ct).ConfigureAwait(false))
            {
                scanned++;
                batch.Add(record);
                if (batch.Count >= batchSize)
                {
                    var (ok, ko) = await FlushAsync(batch, ct).ConfigureAwait(false);
                    indexed += ok;
                    failed += ko;
                    batch.Clear();
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // IAsyncEnumerable cannot return a Result: a store that fails mid-stream surfaces here. Batches flushed so far are done
            // (their flags are cleared); the rest stays pending for the next run.
            LogReindexStreamFailed(_logger, scanned, ex.GetType().Name, ex);
            return Result<ReindexReport, AgentError>.Failure(AgentError.MemoryStoreFailed("Streaming memory records for reindex failed.", ex.GetType().Name));
        }

        if (batch.Count > 0)
        {
            var (ok, ko) = await FlushAsync(batch, ct).ConfigureAwait(false);
            indexed += ok;
            failed += ko;
        }

        return Result<ReindexReport, AgentError>.Success(new ReindexReport(scanned, indexed, failed));
    }

    /// <summary>
    /// Upserts one batch. On upsert failure the whole batch counts as failed (the index writes none of it, so <c>IndexPending</c> stays
    /// set and the next reindex retries). A record whose vector was written but whose flag could not be cleared also counts as failed:
    /// it stays pending and is re-embedded next run.
    /// </summary>
    private async ValueTask<(int Indexed, int Failed)> FlushAsync(List<MemoryRecord> batch, CancellationToken ct)
    {
        var upserted = await index.UpsertAsync(batch, ct).ConfigureAwait(false);
        if (upserted.IsFailure)
        {
            LogReindexBatchFailed(_logger, batch.Count, upserted.Error.ToString());
            return (0, batch.Count);
        }

        var failed = 0;
        foreach (var record in batch)
        {
            if (!record.IndexPending)
            {
                continue;
            }

            var cleared = await store.UpdateAsync(record.Id, new MemoryUpdate { IndexPending = false }, ct).ConfigureAwait(false);
            if (cleared.IsFailure)
            {
                LogClearPendingFailed(_logger, record.Id, cleared.Error.ToString());
                failed++;
            }
        }

        return (batch.Count - failed, failed);
    }

    [LoggerMessage(EventId = 500, Level = LogLevel.Warning, Message = "Memory {Memory} stored but not indexed (pending): {Error}")]
    private static partial void LogIndexPending(ILogger logger, MemoryId memory, string error);

    [LoggerMessage(EventId = 501, Level = LogLevel.Warning, Message = "Refreshing duplicate memory {Memory} failed, inserting instead: {Error}")]
    private static partial void LogDedupeRefreshFailed(ILogger logger, MemoryId memory, string error);

    [LoggerMessage(EventId = 502, Level = LogLevel.Warning, Message = "MarkRecalled failed (recall still returned): {Error}")]
    private static partial void LogMarkRecalledFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 503, Level = LogLevel.Warning, Message = "Removing memory {Memory} from the index failed (stale entry is harmless): {Error}")]
    private static partial void LogIndexRemoveFailed(ILogger logger, MemoryId memory, string error);

    [LoggerMessage(EventId = 504, Level = LogLevel.Warning, Message = "Reindex batch of {Count} failed: {Error}")]
    private static partial void LogReindexBatchFailed(ILogger logger, int count, string error);

    [LoggerMessage(EventId = 505, Level = LogLevel.Warning, Message = "Clearing IndexPending for memory {Memory} failed (next reindex retries): {Error}")]
    private static partial void LogClearPendingFailed(ILogger logger, MemoryId memory, string error);

    [LoggerMessage(EventId = 506, Level = LogLevel.Warning, Message = "Dedupe threshold {Threshold} is not in (0, 1]; dedupe is disabled")]
    private static partial void LogDedupeThresholdInvalid(ILogger logger, double threshold);

    [LoggerMessage(EventId = 507, Level = LogLevel.Warning, Message = "Reindex aborted: the store's record stream threw {ExceptionType} after {Scanned} records (unflushed records stay pending)")]
    private static partial void LogReindexStreamFailed(ILogger logger, int scanned, string exceptionType, Exception exception);

    [LoggerMessage(EventId = 508, Level = LogLevel.Warning, Message = "Recall for owner {Owner} degraded to {Tier}: the semantic index was unavailable or returned no hits")]
    private static partial void LogRecallDegraded(ILogger logger, string owner, MemoryRecallTier tier);
}
