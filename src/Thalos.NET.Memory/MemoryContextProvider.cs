using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Caching;
using Thalos.Runtime;

namespace Thalos.Memory;

/// <summary>
/// Auto-recall: once per agent run (MAF invokes context providers before the run's first model call, not again inside the
/// tool-call loop), recalls memories relevant to the last user message for the turn's caller
/// (<see cref="TurnScope.Caller"/>), this agent and the configured shared owner, and injects them as a delimited
/// <c>&lt;memories&gt;</c> block in a user message placed directly before that user message. The block is per-turn, so
/// it stays out of <see cref="AIContext.Instructions"/> and after the stored history: the instructions and the history
/// form a stable prefix a prompt cache can reuse. The message carries <see cref="PromptCacheHints.Transient"/>, so history
/// stores skip it and it is never replayed on a later turn. When nothing is recalled no message is inserted and the
/// messages are passed on unchanged. The owner is resolved by
/// <see cref="MemoryOwnerResolver.Resolve"/> — the same resolution <see cref="MemoryTools"/> uses for the explicit
/// tools — so this, the primary read path MAF invokes before every turn, never disagrees with what
/// <c>memory__remember</c> just wrote. Recall never fails a turn: any error is logged,
/// a <see cref="MemoryRecallFailedEvent"/> is published and the turn proceeds without memories. Recalled text is
/// untrusted: when an <see cref="IUntrustedContentScanner"/> is available every memory is scanned and quarantined ones are
/// dropped (<see cref="MemoryQuarantinedEvent"/>). Nothing is stored after the turn (explicit writes only).
/// Outside a turn, or for an anonymous/blank caller, the provider does nothing (there is no owner to recall for).
/// </summary>
/// <remarks>
/// This overrides <see cref="AIContextProvider.InvokingCoreAsync"/> rather than <c>ProvideAIContextAsync</c>: MAF 1.22's
/// default merge appends a provider's messages after every input message, which would put the block after the latest user
/// message. Here the provider sets the context's whole message sequence, with the block inserted in place, and returns
/// the context it was given.
/// </remarks>
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
    protected override async ValueTask<AIContext> InvokingCoreAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var input = context.AIContext;

        // materialised once: MAF hands history over as a lazy projection that clones on every enumeration, and the block
        // is placed by the identity of the message recall queried for
        var messages = input.Messages?.ToList();
        if (messages is null || LastUser(ProvideInputMessageFilter(messages)) is not { } latest)
        {
            return input;
        }

        var block = await RecallBlockAsync(latest.Text, cancellationToken).ConfigureAwait(false);
        if (block is null)
        {
            return input;
        }

        var memories = new ChatMessage(ChatRole.User, block) { AdditionalProperties = new() { [PromptCacheHints.Transient] = true } }
            .WithAgentRequestMessageSource(AgentRequestMessageSourceType.AIContextProvider, GetType().FullName);
        messages.Insert(messages.FindLastIndex(m => ReferenceEquals(m, latest)), memories);
        input.Messages = messages; // MAF: providers modify and return the context they were given, so nothing else is dropped
        return input;
    }

    /// <summary>The rendered block for <paramref name="query"/>, or null when there is no owner, nothing is kept or recall fails.</summary>
    private async ValueTask<string?> RecallBlockAsync(string query, CancellationToken cancellationToken)
    {
        var scope = TurnScope.Current;
        if (scope is null || MemoryOwnerResolver.Resolve(scope.Caller) is not { } resolved)
        {
            return null;
        }

        try
        {
            var recalled = await memory.RecallAsync(query, new MemoryScope(resolved.OwnerId, agentId, sharedOwnerId), recall, cancellationToken).ConfigureAwait(false);
            if (recalled.IsFailure)
            {
                LogRecallFailed(_logger, recalled.Error.ToString());
                await PublishAsync((s, t) => new MemoryRecallFailedEvent(s, t, recalled.Error.Code), cancellationToken).ConfigureAwait(false);
                return null;
            }

            var kept = await FilterAsync(recalled.Value.Memories, cancellationToken).ConfigureAwait(false);
            if (kept.Count == 0)
            {
                return null;
            }

            var block = MemoryRecallBlock.Render(kept, clock.GetUtcNow());
            var ids = new MemoryId[kept.Count];
            for (var i = 0; i < kept.Count; i++)
            {
                ids[i] = kept[i].Record.Id;
            }

            await PublishAsync((s, t) => new MemoryRecalledEvent(s, t, ids, block.Length), cancellationToken).ConfigureAwait(false);
            return block;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogRecallThrew(_logger, ex.Message, ex);
            await PublishAsync((s, t) => new MemoryRecallFailedEvent(s, t, AgentErrorCode.MemoryIndexFailed), CancellationToken.None).ConfigureAwait(false);
            return null;
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

    /// <summary>The last user message with non-blank text: the one recall queries for, and the one the block is placed before.</summary>
    internal static ChatMessage? LastUser(IEnumerable<ChatMessage> messages) =>
        messages.LastOrDefault(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text));

    [LoggerMessage(EventId = 510, Level = LogLevel.Warning, Message = "Memory recall failed; the turn continues without memories: {Error}")]
    private static partial void LogRecallFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 511, Level = LogLevel.Warning, Message = "Memory recall threw; the turn continues without memories: {Error}")]
    private static partial void LogRecallThrew(ILogger logger, string error, Exception exception);

    [LoggerMessage(EventId = 512, Level = LogLevel.Warning, Message = "Recalled memory {Memory} was quarantined and dropped: {Detail}")]
    private static partial void LogQuarantined(ILogger logger, MemoryId memory, string detail);

    [LoggerMessage(EventId = 513, Level = LogLevel.Warning, Message = "The untrusted-content scanner threw; the memory is dropped: {Error}")]
    private static partial void LogScannerThrew(ILogger logger, string error, Exception exception);
}
