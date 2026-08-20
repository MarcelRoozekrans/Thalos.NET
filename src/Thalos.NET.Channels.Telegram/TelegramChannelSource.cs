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

    private readonly TelegramBotClient _client;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramChannelSource> _logger;

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
    /// Streams accepted messages until <paramref name="ct"/> is cancelled. Never throws for a transport failure:
    /// a <see cref="TelegramApiException"/> carrying <see cref="TelegramApiException.RetryAfter"/> is honoured by
    /// waiting that long; every other failure (network down, DNS, a 5xx, an unparsable response) is logged and
    /// backed off with a cap, then retried — a channel source that throws out of this method ends that channel
    /// permanently, which for a single-operator bot reads as "the agent stopped answering".
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
    /// bot was added to a group), and a sender present in <see cref="TelegramOptions.AllowedUserIds"/>. A rejected
    /// sender is dropped silently — no reply is sent — because answering "you are not authorised" would confirm to
    /// a prober that the bot is live and worth attacking; the drop is still logged server-side so it is never
    /// invisible to the operator.
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<InboundMessage> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var state = new PollState();

        while (!ct.IsCancellationRequested)
        {
            var updates = await PollAsync(state, ct).ConfigureAwait(false);
            if (updates is null)
            {
                yield break;
            }

            if (updates.Count == 0)
            {
                continue;
            }

            // THE OFFSET RULE: acknowledge the whole batch — advance past its highest update id — before any
            // message from it is yielded/processed below.
            AdvanceOffset(state, updates);

            foreach (var message in AcceptedMessages(updates))
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
    private async Task<IReadOnlyList<TelegramUpdate>?> PollAsync(PollState state, CancellationToken ct)
    {
        try
        {
            var updates = await _client.GetUpdatesAsync(state.Offset, _options.PollTimeoutSeconds, ct).ConfigureAwait(false);
            state.Backoff = InitialBackoff;
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
            LogPollFailed(_logger, ex, state.Backoff);
            var waited = await DelayAsync(state.Backoff, ct).ConfigureAwait(false);
            state.Backoff = Next(state.Backoff);
            return waited ? [] : null;
        }
    }

    /// <summary>Advances <paramref name="state"/>'s offset past the highest update id in <paramref name="updates"/>.</summary>
    private static void AdvanceOffset(PollState state, IReadOnlyList<TelegramUpdate> updates)
    {
        var highest = updates[0].UpdateId;
        foreach (var u in updates)
        {
            if (u.UpdateId > highest)
            {
                highest = u.UpdateId;
            }
        }

        state.Offset = highest + 1;
    }

    /// <summary>Applies the three admission gates to <paramref name="updates"/>, yielding an <see cref="InboundMessage"/> for each that clears all three.</summary>
    private IEnumerable<InboundMessage> AcceptedMessages(IReadOnlyList<TelegramUpdate> updates)
    {
        foreach (var update in updates)
        {
            var message = update.Message;
            if (message is null || string.IsNullOrWhiteSpace(message.Text))
            {
                LogSkippedNoText(_logger, update.UpdateId);
                continue;
            }

            if (!string.Equals(message.Chat.Type, "private", StringComparison.Ordinal))
            {
                LogDroppedNonPrivate(_logger, update.UpdateId, message.Chat.Type);
                continue;
            }

            if (message.From is null || !_options.AllowedUserIds.Contains(message.From.Id))
            {
                // Gate 2 — dropped silently, never answered. Logged here, server-side only, so an operator can
                // still see a probing sender in their own logs without the bot ever confirming to that sender that
                // anything is listening.
                LogRejectedSender(_logger, update.UpdateId);
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

    /// <summary>The mutable poll state (offset, current backoff) threaded through one <see cref="ReadAsync"/> call.</summary>
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

    [LoggerMessage(EventId = 705, Level = LogLevel.Warning, Message = "Update {UpdateId} is from a sender outside AllowedUserIds; dropped silently, no reply sent")]
    private static partial void LogRejectedSender(ILogger logger, long updateId);
}
