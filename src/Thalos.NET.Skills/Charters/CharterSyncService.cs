using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZeroAlloc.Results;

namespace Thalos.Skills.Charters;

/// <summary>
/// Syncs the files under <see cref="CharterOptions.Roots"/> into the <see cref="IRoleCharterStore"/> once, in
/// <see cref="IHostedLifecycleService.StartingAsync"/>, then republishes every version to <paramref name="catalog"/>.
/// </summary>
/// <remarks>
/// <para>Mirrors <see cref="SkillSyncService"/> closely, with two charter-specific differences.</para>
/// <para>
/// A file that fails to load is still counted as <em>seen</em> for its expected role (the role its name or folder
/// claims), not dropped the way a failed skill file is: a refused edit — a typo, a stray <c>tools:</c> key — must
/// never deactivate the version already live for that role. <see cref="RoleCharterFileLoader.ExpectedRole"/> supplies
/// the role even when the file could not be parsed.
/// </para>
/// <para>
/// Every configured <see cref="AgentEnvelope"/> must end the run with an active charter for its role, or
/// <see cref="SyncAsync"/> fails naming the missing role(s) and <see cref="StartingAsync"/> throws, so the host never
/// starts serving an envelope with no instructions. This check runs after <see cref="CharteredAgentCatalog.Set"/>, and
/// against the store's full current state, not just what this run scanned — an envelope's charter having existed
/// since before this process started still counts.
/// </para>
/// </remarks>
/// <param name="store">Where the parsed charters land; the source of truth, and a failure writing it is fatal.</param>
/// <param name="catalog">Republished with every known version on every run via <see cref="CharteredAgentCatalog.Set"/>.</param>
/// <param name="options">Supplies <see cref="CharterOptions.Roots"/> and <see cref="CharterOptions.Envelopes"/>.</param>
/// <param name="clock">Stamps <see cref="RoleCharter.UpdatedAt"/> on everything this run loads.</param>
/// <param name="logger">Optional; a null logger is used when the host registered none.</param>
public sealed partial class CharterSyncService(
    IRoleCharterStore store,
    CharteredAgentCatalog catalog,
    IOptions<CharterOptions> options,
    TimeProvider clock,
    ILogger<CharterSyncService>? logger = null) : IHostedLifecycleService
{
    private readonly ILogger _logger = logger ?? NullLogger<CharterSyncService>.Instance;

    /// <summary>Runs the sync. Throws when it fails, which fails the host start.</summary>
    /// <exception cref="InvalidOperationException">The sync failed — a store failure, or an envelope with no active charter.</exception>
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        var result = await SyncAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"Thalos.NET.Skills: the start-up role-charter sync failed ({result.Error}). A chartered agent with no instructions must never start.");
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

    /// <summary>
    /// Scans every root, upserts what changed, deactivates what disappeared (only when every configured root was
    /// readable), republishes the catalog with the store's full version set, and finally fails when a configured
    /// envelope's role has no active charter.
    /// </summary>
    /// <param name="ct">Cancels the scan between files.</param>
    public async ValueTask<Result<SkillSyncReport, AgentError>> SyncAsync(CancellationToken ct)
    {
        var existing = await store.ListVersionsAsync(ct).ConfigureAwait(false);
        if (existing.IsFailure)
        {
            return Result<SkillSyncReport, AgentError>.Failure(existing.Error);
        }

        var applied = await ScanAndApplyAsync(KnownActive(existing.Value), ct).ConfigureAwait(false);
        return applied.IsFailure ? applied : await PublishAndValidateAsync(applied.Value, ct).ConfigureAwait(false);
    }

    /// <summary>Every role's current active version, before this run.</summary>
    private static Dictionary<string, RoleCharter> KnownActive(IReadOnlyList<RoleCharter> versions)
    {
        var knownActive = new Dictionary<string, RoleCharter>(StringComparer.Ordinal);
        foreach (var version in versions)
        {
            if (version.IsActive)
            {
                knownActive[version.Role] = version;
            }
        }

        return knownActive;
    }

    /// <summary>Scans <see cref="CharterOptions.Roots"/> and applies what changed; skips the sweep when no root was readable.</summary>
    private async ValueTask<Result<SkillSyncReport, AgentError>> ScanAndApplyAsync(Dictionary<string, RoleCharter> knownActive, CancellationToken ct)
    {
        var roots = options.Value.Roots;
        var scan = await ScanAsync(roots, ct).ConfigureAwait(false);
        if (scan.Readable == 0)
        {
            // No root produced a listing: either none is configured (a no-op) or every one is a typo or unreadable.
            // Either way the sweep is skipped, because DeactivateMissingAsync([]) would retire every chartered role.
            if (roots.Count > 0)
            {
                LogNoReadableRoots(_logger, roots.Count);
            }

            return Result<SkillSyncReport, AgentError>.Success(new SkillSyncReport(0, 0, 0, scan.Skipped, 0));
        }

        // The sweep may only run over a *complete* listing; one unreadable root out of several would otherwise have
        // DeactivateMissingAsync retire exactly the roles that root contributes.
        var complete = roots.Count == scan.Readable;
        if (!complete)
        {
            LogSweepSkipped(_logger, roots.Count - scan.Readable, roots.Count);
        }

        return await ApplyAsync(scan, knownActive, complete, ct).ConfigureAwait(false);
    }

    /// <summary>Republishes the catalog from the store's full version set, then fails when a configured envelope's role has no active charter.</summary>
    private async ValueTask<Result<SkillSyncReport, AgentError>> PublishAndValidateAsync(SkillSyncReport report, CancellationToken ct)
    {
        var all = await store.ListVersionsAsync(ct).ConfigureAwait(false);
        if (all.IsFailure)
        {
            return Result<SkillSyncReport, AgentError>.Failure(all.Error);
        }

        catalog.Set(all.Value);

        var missing = MissingRoles(all.Value);
        if (missing.Count > 0)
        {
            var joined = string.Join(", ", missing);
            LogMissingCharters(_logger, joined);
            return Result<SkillSyncReport, AgentError>.Failure(AgentError.Validation(
                $"No active role charter for: {joined}. An envelope with no charter is never silently served as an agent with no instructions."));
        }

        return Result<SkillSyncReport, AgentError>.Success(report);
    }

    /// <summary>Every configured envelope's <see cref="AgentEnvelope.Name"/> that has no active charter in <paramref name="versions"/>.</summary>
    private List<string> MissingRoles(IReadOnlyList<RoleCharter> versions)
    {
        var active = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < versions.Count; i++)
        {
            if (versions[i].IsActive)
            {
                active.Add(versions[i].Role);
            }
        }

        var envelopes = options.Value.Envelopes;
        var missing = new List<string>();
        for (var i = 0; i < envelopes.Count; i++)
        {
            if (!active.Contains(envelopes[i].Name))
            {
                missing.Add(envelopes[i].Name);
            }
        }

        return missing;
    }

    private sealed record Scan(List<RoleCharter> Documents, int Skipped, int Readable, List<string> Seen);

    private async ValueTask<Scan> ScanAsync(IList<string> roots, CancellationToken ct)
    {
        var documents = new List<RoleCharter>();
        var seen = new List<string>();
        var byRole = new Dictionary<string, string>(StringComparer.Ordinal);
        var skipped = 0;
        var readable = 0;
        var now = clock.GetUtcNow();

        for (var r = 0; r < roots.Count; r++)
        {
            var root = roots[r];
            var files = RoleCharterFileLoader.Enumerate(root);
            if (files.IsFailure)
            {
                LogRootUnavailable(_logger, root, files.Error.Message);
                continue;
            }

            readable++;
            for (var i = 0; i < files.Value.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var file = files.Value[i];
                var loaded = await RoleCharterFileLoader.LoadAsync(root, file, now, ct).ConfigureAwait(false);
                if (loaded.IsFailure)
                {
                    skipped++;
                    LogFileSkipped(_logger, loaded.Error.Message);

                    // A refused file must not let the sweep deactivate the version already live for that role: the
                    // file's expected role (from its name or folder) counts as seen even though it failed to parse.
                    if (RoleCharterFileLoader.ExpectedRole(root, file) is { Length: > 0 } expected)
                    {
                        seen.Add(expected);
                    }

                    continue;
                }

                if (byRole.TryGetValue(loaded.Value.Role, out var first))
                {
                    skipped++;
                    LogDuplicateRole(_logger, loaded.Value.Role, first, file);
                    continue;
                }

                byRole[loaded.Value.Role] = file;
                seen.Add(loaded.Value.Role);
                documents.Add(loaded.Value);
            }
        }

        return new Scan(documents, skipped, readable, seen);
    }

    /// <summary>Writes what the scan found, then sweeps.</summary>
    /// <param name="scan">What the roots produced this run.</param>
    /// <param name="knownActive">Every role's current active version before this run.</param>
    /// <param name="complete">Whether every configured root was readable; when false, nothing is deactivated.</param>
    /// <param name="ct">Cancels the store calls.</param>
    private async ValueTask<Result<SkillSyncReport, AgentError>> ApplyAsync(Scan scan, Dictionary<string, RoleCharter> knownActive, bool complete, CancellationToken ct)
    {
        var upserted = 0;
        var unchanged = 0;

        for (var i = 0; i < scan.Documents.Count; i++)
        {
            var charter = scan.Documents[i];
            if (knownActive.TryGetValue(charter.Role, out var current)
                && string.Equals(current.ContentHash, charter.ContentHash, StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }

            var stored = await store.UpsertAsync(charter, ct).ConfigureAwait(false);
            if (stored.IsFailure)
            {
                return Result<SkillSyncReport, AgentError>.Failure(stored.Error);
            }

            upserted++;
        }

        var deactivated = 0;
        if (complete)
        {
            foreach (var role in knownActive.Keys)
            {
                if (!scan.Seen.Contains(role, StringComparer.Ordinal))
                {
                    deactivated++;
                }
            }

            var swept = await store.DeactivateMissingAsync(scan.Seen, ct).ConfigureAwait(false);
            if (swept.IsFailure)
            {
                return Result<SkillSyncReport, AgentError>.Failure(swept.Error);
            }
        }

        LogSynced(_logger, scan.Documents.Count, upserted, unchanged, scan.Skipped, deactivated);
        return Result<SkillSyncReport, AgentError>.Success(new SkillSyncReport(scan.Documents.Count, upserted, unchanged, scan.Skipped, deactivated));
    }

    [LoggerMessage(EventId = 600, Level = LogLevel.Information, Message = "Role charter sync: {Scanned} scanned, {Upserted} upserted, {Unchanged} unchanged, {Skipped} skipped, {Deactivated} deactivated")]
    private static partial void LogSynced(ILogger logger, int scanned, int upserted, int unchanged, int skipped, int deactivated);

    [LoggerMessage(EventId = 601, Level = LogLevel.Warning, Message = "Role charter file skipped: {Error}")]
    private static partial void LogFileSkipped(ILogger logger, string error);

    [LoggerMessage(EventId = 602, Level = LogLevel.Warning, Message = "Role charter root '{Root}' unavailable and ignored: {Error}")]
    private static partial void LogRootUnavailable(ILogger logger, string root, string error);

    [LoggerMessage(EventId = 603, Level = LogLevel.Warning, Message = "Duplicate role '{Role}': '{First}' wins, '{Second}' is ignored (roots are searched in order)")]
    private static partial void LogDuplicateRole(ILogger logger, string role, string first, string second);

    [LoggerMessage(EventId = 604, Level = LogLevel.Error, Message = "None of the {Count} configured role-charter roots could be read; nothing was synced and no role was deactivated")]
    private static partial void LogNoReadableRoots(ILogger logger, int count);

    [LoggerMessage(EventId = 605, Level = LogLevel.Warning, Message = "{Failed} of {Total} role-charter roots could not be read, so nothing was deactivated this run; the roles they contribute stay active until every root is readable again")]
    private static partial void LogSweepSkipped(ILogger logger, int failed, int total);

    [LoggerMessage(EventId = 606, Level = LogLevel.Error, Message = "No active role charter for: {Roles}")]
    private static partial void LogMissingCharters(ILogger logger, string roles);
}
