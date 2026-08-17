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

    // Task 10 replaces this stub with the real dedupe lookup.
    private static ValueTask<MemoryRecord?> FindDuplicateAsync(MemoryRecord candidate, double threshold, CancellationToken ct) => new((MemoryRecord?)null);

    // Tasks 11–12 implement these (MA0025 forbids NotImplementedException; these bodies fail every test until then).
    public ValueTask<Result<IReadOnlyList<RecalledMemory>, AgentError>> RecallAsync(string query, MemoryScope scope, RecallOptions options, CancellationToken ct) =>
        new(Result<IReadOnlyList<RecalledMemory>, AgentError>.Failure(AgentError.MemoryValidationFailed("not implemented")));

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
}
