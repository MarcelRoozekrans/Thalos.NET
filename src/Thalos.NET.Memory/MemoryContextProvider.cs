using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Runtime;
using ZeroAlloc.Authorization;

namespace Thalos.Memory;

/// <summary>
/// Auto-recall: once per agent run (MAF invokes context providers before the run's first model call, not again inside the
/// tool-call loop), recalls memories relevant to the last user message for the turn's caller
/// (<see cref="TurnScope.Caller"/>), this agent and the configured shared owner, and injects them as a delimited
/// <c>&lt;memories&gt;</c> block via <see cref="AIContext.Instructions"/>. Recall never fails a turn: any error is logged,
/// a <see cref="MemoryRecallFailedEvent"/> is published and the turn proceeds without memories. Recalled text is
/// untrusted: when an <see cref="IUntrustedContentScanner"/> is available every memory is scanned and quarantined ones are
/// dropped (<see cref="MemoryQuarantinedEvent"/>). Nothing is stored after the turn (explicit writes only).
/// Outside a turn, or for an anonymous/blank caller, the provider does nothing (there is no owner to recall for).
/// </summary>
public sealed partial class MemoryContextProvider(
    IMemoryService memory,
    AgentId agentId,
    RecallOptions recall,
    string? sharedOwnerId,
    TimeProvider clock,
    AgentEventHub hub,
    IUntrustedContentScanner? scanner = null,
    ILogger<MemoryContextProvider>? logger = null) : AIContextProvider
{
    private readonly ILogger _logger = logger ?? NullLogger<MemoryContextProvider>.Instance;

    /// <summary>The recall budget this provider applies (tests: verifies the per-agent copy).</summary>
    internal RecallOptions Recall => recall;

    /// <inheritdoc />
    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scope = TurnScope.Current;
        var owner = scope?.Caller.Id;
        if (scope is null || string.IsNullOrWhiteSpace(owner) || string.Equals(owner, AnonymousSecurityContext.AnonymousId, StringComparison.Ordinal))
        {
            return new AIContext();
        }

        var query = LastUserText(context.AIContext.Messages);
        if (string.IsNullOrWhiteSpace(query))
        {
            return new AIContext();
        }

        try
        {
            var recalled = await memory.RecallAsync(query, new MemoryScope(owner, agentId, sharedOwnerId), recall, cancellationToken).ConfigureAwait(false);
            if (recalled.IsFailure)
            {
                LogRecallFailed(_logger, recalled.Error.ToString());
                await PublishAsync((s, t) => new MemoryRecallFailedEvent(s, t, recalled.Error.Code), cancellationToken).ConfigureAwait(false);
                return new AIContext();
            }

            var kept = await FilterAsync(recalled.Value, cancellationToken).ConfigureAwait(false);
            if (kept.Count == 0)
            {
                return new AIContext();
            }

            var block = MemoryRecallBlock.Render(kept, clock.GetUtcNow());
            var ids = new MemoryId[kept.Count];
            for (var i = 0; i < kept.Count; i++)
            {
                ids[i] = kept[i].Record.Id;
            }

            await PublishAsync((s, t) => new MemoryRecalledEvent(s, t, ids, block.Length), cancellationToken).ConfigureAwait(false);
            return new AIContext { Instructions = block };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogRecallThrew(_logger, ex.Message, ex);
            await PublishAsync((s, t) => new MemoryRecallFailedEvent(s, t, AgentErrorCode.MemoryIndexFailed), CancellationToken.None).ConfigureAwait(false);
            return new AIContext();
        }
    }

    /// <summary>Drops memories the scanner quarantines (a scanner exception counts as a denial — fail closed).</summary>
    private async ValueTask<List<RecalledMemory>> FilterAsync(IReadOnlyList<RecalledMemory> recalled, CancellationToken ct)
    {
        var kept = new List<RecalledMemory>(recalled.Count);
        foreach (var m in recalled)
        {
            if (scanner is not null)
            {
                var verdict = await ScanAsync(m.Record.Text, ct).ConfigureAwait(false);
                if (!verdict.Allowed)
                {
                    LogQuarantined(_logger, m.Record.Id, verdict.Detail ?? "unknown");
                    await PublishAsync((s, t) => new MemoryQuarantinedEvent(s, t, m.Record.Id, verdict.Detail), ct).ConfigureAwait(false);
                    continue;
                }
            }

            kept.Add(m);
        }

        return kept;
    }

    private async ValueTask<UntrustedContentVerdict> ScanAsync(string text, CancellationToken ct)
    {
        try
        {
            return await scanner!.ScanAsync(text, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogScannerThrew(_logger, ex.Message, ex);
            return UntrustedContentVerdict.Quarantine("scanner failed: " + ex.GetType().Name);
        }
    }

    /// <summary>Publishes into the current turn (streamed + hub); the provider only gets this far inside a turn.</summary>
    private ValueTask PublishAsync(Func<SessionId, TurnId, AgentEvent> make, CancellationToken ct) => MemoryEvents.PublishAsync(hub, make, ct);

    internal static string? LastUserText(IEnumerable<ChatMessage>? messages) =>
        messages?.LastOrDefault(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text))?.Text;

    [LoggerMessage(EventId = 510, Level = LogLevel.Warning, Message = "Memory recall failed; the turn continues without memories: {Error}")]
    private static partial void LogRecallFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 511, Level = LogLevel.Warning, Message = "Memory recall threw; the turn continues without memories: {Error}")]
    private static partial void LogRecallThrew(ILogger logger, string error, Exception exception);

    [LoggerMessage(EventId = 512, Level = LogLevel.Warning, Message = "Recalled memory {Memory} was quarantined and dropped: {Detail}")]
    private static partial void LogQuarantined(ILogger logger, MemoryId memory, string detail);

    [LoggerMessage(EventId = 513, Level = LogLevel.Warning, Message = "The untrusted-content scanner threw; the memory is dropped: {Error}")]
    private static partial void LogScannerThrew(ILogger logger, string error, Exception exception);
}
