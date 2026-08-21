using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Thalos.Channels.Telegram;

/// <summary>
/// Long-polls Telegram's <c>getUpdates</c> and turns accepted updates into <see cref="InboundMessage"/>s for the
/// channel pump. This is the inbound half of the Telegram channel and its security boundary: every message that
/// reaches the pump through here has already cleared three admission gates (private chat, allow-listed sender,
/// non-blank text) — nothing past this type re-checks who is allowed to talk to the agent.
/// </summary>
/// <remarks>
/// <see cref="TelegramBotClient"/> is a pure transport: it owns no timing. Poll cadence, flood-control backoff and
/// transport-failure resilience are this type's responsibility, described alongside <see cref="ReadAsync"/> below.
/// </remarks>
public sealed partial class TelegramChannelSource : IChannelSource
{
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The minimum wait between two successful-but-empty polls. Telegram's real long-poll (<c>PollTimeoutSeconds</c>,
    /// default 50s) normally hides the need for this: a genuine "nothing arrived" response only comes back after
    /// Telegram itself held the request open. But nothing here can assume the far end always behaves that way — an
    /// error page, a misconfigured proxy, or (as this package's own tests do) a stub can return an empty result
    /// instantly and forever, and without a floor <see cref="ReadAsync"/> would spin at 100% CPU re-issuing
    /// <c>getUpdates</c> as fast as the runtime can schedule it.
    /// </summary>
    private static readonly TimeSpan MinPollInterval = TimeSpan.FromMilliseconds(200);

    private readonly TelegramBotClient _client;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramChannelSource> _logger;
    private readonly PollState _state = new();

    /// <summary>Creates a source that polls through <paramref name="client"/> using <paramref name="options"/>.</summary>
    /// <param name="client">The Bot API transport. Its token must match <paramref name="options"/>'s <c>BotToken</c>.</param>
    /// <param name="options">
    /// The channel's configuration, including the allow-list every sender is checked against. Not validated here —
    /// registration is expected to bind these through <c>TelegramOptions.Describe</c> with <c>ValidateOnStart</c>,
    /// the same pattern <c>Thalos.NET.Channels</c> uses for <c>ChannelOptions</c>.
    /// </param>
    /// <param name="logger">Where poll failures, flood-control waits and dropped updates are logged.</param>
    public TelegramChannelSource(TelegramBotClient client, TelegramOptions options, ILogger<TelegramChannelSource> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _client = client;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ChannelId => "telegram";

    /// <summary>
    /// The lowest update id not yet acknowledged to Telegram. Test-only hook, the same shape as
    /// <c>ChannelPump.IsTurnRunning</c>: nothing externally observable through <see cref="ReadAsync"/> alone can
    /// distinguish "offset advanced before the message was yielded" from "offset advanced after" — both orderings
    /// produce the same sequence of HTTP requests — so a test needs to read this mid-stream, immediately after
    /// consuming one message and before pulling the next, to prove which ordering actually happened.
    /// </summary>
    internal long CurrentOffset => _state.Offset;

    /// <summary>
    /// Streams accepted messages until <paramref name="ct"/> is cancelled. Never throws for a transport failure:
    /// a <see cref="TelegramApiException"/> carrying <see cref="TelegramApiException.RetryAfter"/> is honoured by
    /// waiting that long; every other failure (network down, DNS, a 5xx, an unparsable response, a malformed
    /// response body that would otherwise NRE while it is being turned into messages) is logged and handled without
    /// ever letting an exception leave this method — a channel source that throws out of this method ends that
    /// channel permanently, which for a single-operator bot reads as "the agent stopped answering".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The offset rule.</b> After a batch of updates arrives, the offset is advanced past the highest update id
    /// in that batch <em>before</em> any message from it is yielded — not after the message is processed. This
    /// makes delivery to the pump at-most-once: if the process crashes mid-turn, the update that triggered that
    /// turn is never redelivered on restart. The alternative (acknowledge after processing) is at-least-once, and
    /// would silently re-run a turn that may already have written a memory or touched a repository — a message the
    /// operator can see in their own chat history and retype is the cheaper failure to lose.
    /// </para>
    /// <para>
    /// <b>The three admission gates</b>, applied per update after the offset for its batch has already advanced:
    /// non-blank <c>text</c> (a photo, sticker or join has none), a <c>private</c> chat (anything else means the
    /// bot was added to a group, and a missing/<see langword="null"/> chat fails this gate the same way), and a
    /// sender present in <see cref="TelegramOptions.AllowedUserIds"/>. A rejected sender is dropped silently — no
    /// reply is sent — because answering "you are not authorised" would confirm to a prober that the bot is live
    /// and worth attacking; the drop is still logged server-side, including the sender's id, so it is never
    /// invisible to the operator.
    /// </para>
    /// <para>
    /// <b>Malformed data fails closed, not open.</b> System.Text.Json's non-nullable-annotated properties are
    /// compile-time hints only — a response body missing <c>chat</c>, or an updates array containing a bare JSON
    /// <c>null</c>, deserializes without error into a reference that is <see langword="null"/> at runtime despite
    /// its static type. Every such dereference is guarded, and — because it is cheaper to be sure than to be right
    /// about which guards are exhaustive — the whole per-batch admission pass also runs inside a
    /// <see langword="try"/>/<see langword="catch"/> that skips just that batch on any unexpected exception; a
    /// <see langword="yield return"/> cannot itself sit inside a <see langword="try"/> with a
    /// <see langword="catch"/>, so the accepted messages for a batch are fully computed into a list first and only
    /// yielded afterwards, outside that guard.
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<InboundMessage> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var updates = await PollAsync(ct).ConfigureAwait(false);
            if (updates is null)
            {
                yield break;
            }

            if (updates.Count == 0)
            {
                if (!await DelayAsync(MinPollInterval, ct).ConfigureAwait(false))
                {
                    yield break;
                }

                continue;
            }

            var accepted = ProcessBatch(updates);

            foreach (var message in accepted)
            {
                yield return message;
            }
        }
    }

    /// <summary>
    /// Runs one <c>getUpdates</c> long-poll and classifies the outcome: the updates on success (possibly empty,
    /// when the long-poll simply timed out with nothing new); an empty list after honouring a flood-control
    /// <see cref="TelegramApiException.RetryAfter"/> or backing off from any other transport failure; or
    /// <see langword="null"/> when <paramref name="ct"/> fired, meaning the caller must stop.
    /// </summary>
    private async Task<IReadOnlyList<TelegramUpdate>?> PollAsync(CancellationToken ct)
    {
        try
        {
            var updates = await _client.GetUpdatesAsync(_state.Offset, _options.PollTimeoutSeconds, ct).ConfigureAwait(false);
            _state.Backoff = InitialBackoff;
            return updates;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (TelegramApiException ex) when (ex.RetryAfter is { } retryAfter)
        {
            LogFloodControl(_logger, retryAfter);
            return await DelayAsync(retryAfter, ct).ConfigureAwait(false) ? [] : null;
        }
        catch (Exception ex)
        {
            // Everything else: TelegramApiException without a RetryAfter, HttpRequestException, an
            // HttpClient-internal timeout (itself an OperationCanceledException NOT caused by our own ct, so it did
            // not match the clean-shutdown catch above), a bad response body — all treated the same way, because
            // none of them may end this channel.
            LogPollFailed(_logger, ex, _state.Backoff);
            var waited = await DelayAsync(_state.Backoff, ct).ConfigureAwait(false);
            _state.Backoff = Next(_state.Backoff);
            return waited ? [] : null;
        }
    }

    /// <summary>
    /// Advances the offset past <paramref name="updates"/>' highest update id (THE OFFSET RULE — see
    /// <see cref="ReadAsync"/>), then applies the three admission gates and materializes every accepted message
    /// into a list. Wrapped in its own <see langword="try"/>/<see langword="catch"/> — deliberately not inline in
    /// <see cref="ReadAsync"/>, where a <see langword="yield return"/> would make a surrounding
    /// <see langword="catch"/> illegal — so that any unexpected exception here (a defect this method's null guards
    /// failed to anticipate) skips just this one batch instead of ending the channel.
    /// </summary>
    private List<InboundMessage> ProcessBatch(IReadOnlyList<TelegramUpdate> updates)
    {
        try
        {
            AdvanceOffset(updates);
            return [.. AcceptedMessages(updates)];
        }
        catch (Exception ex)
        {
            LogBatchFailed(_logger, ex);
            return [];
        }
    }

    /// <summary>
    /// Advances <see cref="CurrentOffset"/> past the highest update id in <paramref name="updates"/>. A bare JSON
    /// <c>null</c> array element (Telegram sending garbage, or a test proving this can't crash the loop) carries no
    /// update id and is skipped for this computation, not treated as id 0; if every element is such a null, the
    /// offset does not move, which is the fail-closed choice — advancing past ids nobody can identify would risk
    /// silently skipping a real update this method simply couldn't read.
    /// </summary>
    private void AdvanceOffset(IReadOnlyList<TelegramUpdate> updates)
    {
        long? highest = null;
        foreach (var u in updates)
        {
            if (u is not null && (highest is null || u.UpdateId > highest))
            {
                highest = u.UpdateId;
            }
        }

        if (highest is { } h)
        {
            _state.Offset = h + 1;
        }
    }

    /// <summary>Applies the three admission gates to <paramref name="updates"/>, yielding an <see cref="InboundMessage"/> for each that clears all three.</summary>
    private IEnumerable<InboundMessage> AcceptedMessages(IReadOnlyList<TelegramUpdate> updates)
    {
        foreach (var update in updates)
        {
            if (update is null)
            {
                // A bare JSON null in the updates array — no id, no message, nothing to act on or even log by id.
                LogMalformedUpdate(_logger);
                continue;
            }

            var message = update.Message;
            if (message is null || string.IsNullOrWhiteSpace(message.Text))
            {
                LogSkippedNoText(_logger, update.UpdateId);
                continue;
            }

            // message.Chat is annotated non-nullable, but System.Text.Json enforces nothing at runtime: a response
            // missing "chat" deserializes it to null anyway. The null check short-circuits before .Type is ever
            // dereferenced, so a missing chat fails this gate exactly like any other non-private chat — fail closed.
            if (message.Chat is null || !string.Equals(message.Chat.Type, "private", StringComparison.Ordinal))
            {
                LogDroppedNonPrivate(_logger, update.UpdateId, message.Chat?.Type ?? "(missing)");
                continue;
            }

            if (message.From is null || !_options.AllowedUserIds.Contains(message.From.Id))
            {
                // Gate 2 — dropped silently, never answered. Logged here, server-side only — including the
                // sender's own id, which is the entire value of this line — so an operator can see exactly who
                // probed the bot without it ever confirming to that sender that anything is listening.
                LogRejectedSender(_logger, update.UpdateId, message.From?.Id.ToString(CultureInfo.InvariantCulture) ?? "(none)");
                continue;
            }

            yield return new InboundMessage(
                ChannelId,
                new ConversationId(message.Chat.Id.ToString(CultureInfo.InvariantCulture)),
                message.Text,
                new ConfiguredSecurityContext(_options.PrincipalId, _options.Roles),
                message.MessageId.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Waits for <paramref name="delay"/>, returning <see langword="false"/> instead of throwing when <paramref name="ct"/> fires meanwhile.</summary>
    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Doubles <paramref name="current"/>, capped at <see cref="MaxBackoff"/>.</summary>
    private static TimeSpan Next(TimeSpan current)
    {
        var doubled = current + current;
        return doubled > MaxBackoff ? MaxBackoff : doubled;
    }

    /// <summary>The mutable poll state (offset, current backoff) for this source's one <see cref="ReadAsync"/> stream.</summary>
    private sealed class PollState
    {
        /// <summary>The lowest update id not yet acknowledged.</summary>
        public long Offset { get; set; }

        /// <summary>How long the next transport-failure backoff should wait.</summary>
        public TimeSpan Backoff { get; set; } = InitialBackoff;
    }

    [LoggerMessage(EventId = 701, Level = LogLevel.Warning, Message = "Telegram asked us to slow down; waiting {RetryAfter} before the next getUpdates")]
    private static partial void LogFloodControl(ILogger logger, TimeSpan retryAfter);

    [LoggerMessage(EventId = 702, Level = LogLevel.Error, Message = "getUpdates failed; backing off {Backoff} before retrying")]
    private static partial void LogPollFailed(ILogger logger, Exception ex, TimeSpan backoff);

    [LoggerMessage(EventId = 703, Level = LogLevel.Debug, Message = "Update {UpdateId} carries no text; skipped")]
    private static partial void LogSkippedNoText(ILogger logger, long updateId);

    [LoggerMessage(EventId = 704, Level = LogLevel.Debug, Message = "Update {UpdateId} is not a private chat (type {ChatType}); dropped")]
    private static partial void LogDroppedNonPrivate(ILogger logger, long updateId, string chatType);

    [LoggerMessage(EventId = 705, Level = LogLevel.Warning, Message = "Update {UpdateId} is from sender {SenderId}, outside AllowedUserIds; dropped silently, no reply sent")]
    private static partial void LogRejectedSender(ILogger logger, long updateId, string senderId);

    [LoggerMessage(EventId = 706, Level = LogLevel.Warning, Message = "getUpdates returned a null entry in its updates array; skipped")]
    private static partial void LogMalformedUpdate(ILogger logger);

    [LoggerMessage(EventId = 707, Level = LogLevel.Error, Message = "Processing a batch of updates failed unexpectedly; that batch is skipped and the channel keeps reading")]
    private static partial void LogBatchFailed(ILogger logger, Exception ex);
}
