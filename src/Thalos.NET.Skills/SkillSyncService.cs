using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZeroAlloc.Results;

namespace Thalos.Skills;

/// <summary>
/// Syncs the files under <see cref="SkillOptions.Roots"/> into the <see cref="ISkillStore"/> once, in
/// <see cref="IHostedLifecycleService.StartingAsync"/> — before any other hosted service starts, so the catalogue is populated
/// before the first turn. Files are the source of truth and the sync is one-way: nothing ever writes back to disk.
/// </summary>
/// <remarks>
/// <para>
/// A file that fails to load is logged and skipped, never fatal — one malformed skill must not stop a host. A <em>store</em>
/// failure is fatal: an agent silently missing its procedures is worse than a host that will not start.
/// </para>
/// <para>
/// The <see cref="ISkillIndex"/> is fed from the same run, but only as a best effort — an embedding backend that is down
/// degrades <c>skills__search</c> and nothing else, because the catalogue in the agent's instructions stays authoritative.
/// </para>
/// <para>The service holds no state, so a host may resolve or construct another instance and call <see cref="SyncAsync"/> itself.</para>
/// </remarks>
/// <param name="store">Where the parsed documents land; the source of truth, and a failure writing it is fatal.</param>
/// <param name="index">The search cache, refilled from the store on every run; a failure here only degrades <c>skills__search</c>.</param>
/// <param name="options">Supplies <see cref="SkillOptions.Roots"/>, <see cref="SkillOptions.Enabled"/> and <see cref="SkillOptions.SyncOnStartup"/>.</param>
/// <param name="clock">Stamps <see cref="SkillDocument.UpdatedAt"/> on everything this run writes.</param>
/// <param name="logger">Optional; a null logger is used when the host registered none.</param>
public sealed partial class SkillSyncService(
    ISkillStore store,
    ISkillIndex index,
    IOptions<SkillOptions> options,
    TimeProvider clock,
    ILogger<SkillSyncService>? logger = null) : IHostedLifecycleService
{
    private readonly ILogger _logger = logger ?? NullLogger<SkillSyncService>.Instance;

    /// <summary>Runs the sync. Throws when it fails, which fails the host start.</summary>
    /// <exception cref="InvalidOperationException">The skill store could not be written.</exception>
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (!o.Enabled || !o.SyncOnStartup)
        {
            LogSyncDisabled(_logger);
            return;
        }

        var result = await SyncAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Thalos.NET.Skills: the start-up skill sync failed ({result.Error}). Skills are configuration an agent needs, so the host does not start without them.");
        }
    }

    /// <summary>No-op — the work happens in <see cref="StartingAsync"/>.</summary>
    /// <param name="cancellationToken">Ignored.</param>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>No-op.</summary>
    /// <param name="cancellationToken">Ignored.</param>
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>No-op.</summary>
    /// <param name="cancellationToken">Ignored.</param>
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>No-op.</summary>
    /// <param name="cancellationToken">Ignored.</param>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>No-op.</summary>
    /// <param name="cancellationToken">Ignored.</param>
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Scans every root, upserts what changed, deactivates what disappeared, and reports what it did.</summary>
    /// <param name="ct">Cancels the scan between files.</param>
    /// <returns>What the run did, or the store failure that stopped it.</returns>
    public async ValueTask<Result<SkillSyncReport, AgentError>> SyncAsync(CancellationToken ct)
    {
        var roots = options.Value.Roots;
        var existing = await store.ListAsync(new SkillQuery { IncludeInactive = true }, ct).ConfigureAwait(false);
        if (existing.IsFailure)
        {
            return Result<SkillSyncReport, AgentError>.Failure(existing.Error);
        }

        var known = new Dictionary<SkillName, SkillDocument>();
        foreach (var skill in existing.Value)
        {
            known[skill.Name] = skill;
        }

        var scan = await ScanAsync(roots, ct).ConfigureAwait(false);
        if (scan.Readable == 0)
        {
            // No root produced a listing: either none is configured (a no-op) or every one is a typo or unreadable.
            // Either way the sweep is skipped, because DeactivateMissingAsync([]) would retire the whole library.
            if (roots.Count > 0)
            {
                LogNoReadableRoots(_logger, roots.Count);
            }

            return Result<SkillSyncReport, AgentError>.Success(new SkillSyncReport(0, 0, 0, scan.Skipped, 0));
        }

        return await ApplyAsync(scan, known, ct).ConfigureAwait(false);
    }

    private sealed record Scan(List<SkillDocument> Documents, int Skipped, int Readable);

    private async ValueTask<Scan> ScanAsync(IList<string> roots, CancellationToken ct)
    {
        var documents = new List<SkillDocument>();
        var byName = new Dictionary<SkillName, string>();
        var skipped = 0;
        var readable = 0;
        var now = clock.GetUtcNow();

        for (var r = 0; r < roots.Count; r++)
        {
            var root = roots[r];
            var files = SkillFileLoader.Enumerate(root);
            if (files.IsFailure)
            {
                LogRootUnavailable(_logger, root, files.Error.Message);
                continue;
            }

            readable++;
            for (var i = 0; i < files.Value.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var loaded = await SkillFileLoader.LoadAsync(root, files.Value[i], now, ct).ConfigureAwait(false);
                if (loaded.IsFailure)
                {
                    skipped++;
                    LogFileSkipped(_logger, loaded.Error.Message);
                    continue;
                }

                if (byName.TryGetValue(loaded.Value.Name, out var first))
                {
                    skipped++;
                    // Both paths are logged in full, not root-relative: two copies of the same skill in two roots
                    // usually share their relative path, so only the root tells the operator which file lost.
                    LogDuplicateName(_logger, loaded.Value.Name.Value, first, files.Value[i]);
                    continue;
                }

                byName[loaded.Value.Name] = files.Value[i];
                documents.Add(loaded.Value);
            }
        }

        return new Scan(documents, skipped, readable);
    }

    private async ValueTask<Result<SkillSyncReport, AgentError>> ApplyAsync(Scan scan, Dictionary<SkillName, SkillDocument> known, CancellationToken ct)
    {
        var upserted = 0;
        var unchanged = 0;
        var seen = new List<SkillName>(scan.Documents.Count);

        for (var i = 0; i < scan.Documents.Count; i++)
        {
            var document = scan.Documents[i];
            seen.Add(document.Name);
            if (known.TryGetValue(document.Name, out var current)
                && current.IsActive
                && string.Equals(current.ContentHash, document.ContentHash, StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }

            var stored = await store.UpsertAsync(document, ct).ConfigureAwait(false);
            if (stored.IsFailure)
            {
                return Result<SkillSyncReport, AgentError>.Failure(stored.Error);
            }

            upserted++;
        }

        var deactivated = 0;
        foreach (var (name, skill) in known)
        {
            if (skill.IsActive && !seen.Contains(name))
            {
                deactivated++;
            }
        }

        var swept = await store.DeactivateMissingAsync(seen, ct).ConfigureAwait(false);
        if (swept.IsFailure)
        {
            return Result<SkillSyncReport, AgentError>.Failure(swept.Error);
        }

        await RefreshIndexAsync(seen, known, ct).ConfigureAwait(false);

        LogSynced(_logger, scan.Documents.Count, upserted, unchanged, scan.Skipped, deactivated);
        return Result<SkillSyncReport, AgentError>.Success(new SkillSyncReport(scan.Documents.Count, upserted, unchanged, scan.Skipped, deactivated));
    }

    /// <summary>
    /// Refills the index from the store's active set and drops the vectors of skills that just disappeared. The index is a
    /// rebuildable cache that does not survive the process while the store does, so the content-hash skip governs the store
    /// upsert only: every active skill is re-embedded on every run, or a restart over an unmodified repository would leave
    /// <c>skills__search</c> with nothing to search. Best effort throughout — a failure is logged and only degrades search.
    /// </summary>
    /// <param name="seen">Every name that loaded this run.</param>
    /// <param name="known">Everything the store held before this run, active and inactive.</param>
    /// <param name="ct">Cancels the embedding calls.</param>
    private async ValueTask RefreshIndexAsync(List<SkillName> seen, Dictionary<SkillName, SkillDocument> known, CancellationToken ct)
    {
        foreach (var (name, skill) in known)
        {
            if (skill.IsActive && !seen.Contains(name))
            {
                var removed = await index.RemoveAsync(name, ct).ConfigureAwait(false);
                if (removed.IsFailure)
                {
                    LogIndexFailed(_logger, removed.Error.ToString());
                }
            }
        }

        var active = await store.ListAsync(new SkillQuery(), ct).ConfigureAwait(false);
        if (active.IsFailure)
        {
            LogIndexFailed(_logger, active.Error.ToString());
            return;
        }

        var indexed = await index.UpsertAsync(active.Value, ct).ConfigureAwait(false);
        if (indexed.IsFailure)
        {
            LogIndexFailed(_logger, indexed.Error.ToString());
        }
    }

    [LoggerMessage(EventId = 563, Level = LogLevel.Warning, Message = "Skill index refresh failed; the catalogue still works but skills__search may be incomplete: {Error}")]
    private static partial void LogIndexFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 560, Level = LogLevel.Information, Message = "Skill sync: {Scanned} scanned, {Upserted} upserted, {Unchanged} unchanged, {Skipped} skipped, {Deactivated} deactivated")]
    private static partial void LogSynced(ILogger logger, int scanned, int upserted, int unchanged, int skipped, int deactivated);

    [LoggerMessage(EventId = 561, Level = LogLevel.Warning, Message = "Skill file skipped: {Error}")]
    private static partial void LogFileSkipped(ILogger logger, string error);

    [LoggerMessage(EventId = 562, Level = LogLevel.Warning, Message = "Skill root '{Root}' unavailable and ignored: {Error}")]
    private static partial void LogRootUnavailable(ILogger logger, string root, string error);

    [LoggerMessage(EventId = 565, Level = LogLevel.Warning, Message = "Duplicate skill name '{Skill}': '{First}' wins, '{Second}' is ignored (roots are searched in order)")]
    private static partial void LogDuplicateName(ILogger logger, string skill, string first, string second);

    [LoggerMessage(EventId = 566, Level = LogLevel.Error, Message = "None of the {Count} configured skill roots could be read; nothing was synced and no skill was deactivated")]
    private static partial void LogNoReadableRoots(ILogger logger, int count);

    [LoggerMessage(EventId = 567, Level = LogLevel.Information, Message = "Skill sync is disabled (Thalos:Skills Enabled or SyncOnStartup is false)")]
    private static partial void LogSyncDisabled(ILogger logger);
}
