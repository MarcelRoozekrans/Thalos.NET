using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Thalos.Channels.Telegram;

/// <summary>
/// Renders a streaming agent turn into Telegram messages: the outbound half of the Telegram channel. The pump
/// delivers <em>cumulative</em> renders — every <see cref="TextDeltaEvent"/> carries the whole reply so far — so the
/// first render of a turn sends one message and every later render edits that same message in place, which is what
/// makes a streamed reply look like it is being typed rather than arriving as a wall of fragments.
/// </summary>
/// <remarks>
/// <para>
/// <b>Addressing.</b> The <see cref="ConversationId"/> <em>is</em> the Telegram chat id — that is exactly what
/// <c>TelegramChannelSource</c> puts in it — so a delivery needs no lookup and cannot fail to resolve. This is why
/// the seam is keyed on the conversation: an operator notice (<c>/help</c>, "still working", "that session had
/// already ended") belongs to a chat and often to no session at all, and a session-keyed seam could only address
/// those by inventing an id that resolves to nothing, which meant dropping them.
/// </para>
/// <para>
/// <b>Nothing escapes.</b> The pump calls this from inside a running turn, and an exception thrown here would end
/// that channel; every failure path therefore logs and degrades. Telegram rejecting a MarkdownV2 body with a
/// <c>400</c> costs the formatting, not the answer: the render is retried exactly once with no <c>parse_mode</c> and
/// the unescaped text.
/// </para>
/// <para>
/// <b>Thread safety.</b> Per-turn state is keyed by <see cref="ConversationId"/> in a concurrent dictionary, because
/// several conversations can be mid-turn at once. Deliveries to ONE conversation are serialised by a per-conversation
/// gate, and that is load-bearing rather than belt-and-braces: the pump sends operator notices from its reader loop
/// while a turn streams on a detached task, so two deliveries genuinely do race for the same conversation. Without
/// the gate a notice could clear <c>MessageIds</c> in the window between the bounds check and the indexer inside
/// <see cref="PublishAsync"/> — an <see cref="ArgumentOutOfRangeException"/>, which does not match that method's
/// <c>400</c>-only filter, so it would land in <see cref="DeliverAsync"/>'s catch-all and DROP the render. When the
/// dropped render is the terminal one, the agent's final answer never reaches the chat at all. Deliveries to
/// DIFFERENT conversations never contend: each has its own gate.
/// </para>
/// </remarks>
public sealed partial class TelegramChannelAdapter : IChannelAdapter
{
    private const string ChannelName = "telegram";
    private const string MarkdownV2 = "MarkdownV2";
    private const string TypingAction = "typing";

    /// <summary>
    /// The shortest gap between two <c>sendChatAction</c> calls for one conversation. Telegram's "typing…" indicator
    /// lasts about five seconds, so refreshing it a little sooner keeps it alive across a slow turn — while a render
    /// (which can arrive several times a second) never triggers one of its own.
    /// </summary>
    private static readonly TimeSpan TypingInterval = TimeSpan.FromSeconds(4);

    private readonly TelegramBotClient _client;
    private readonly TimeProvider _clock;
    private readonly ILogger<TelegramChannelAdapter> _logger;
    private readonly ConcurrentDictionary<ConversationId, TurnState> _turns = new();

    /// <summary>
    /// One gate per conversation, so deliveries to the same chat run one at a time while different chats stay fully
    /// parallel. Entries are never removed: a conversation id is a stable Telegram chat, so this is bounded by the
    /// number of chats the bot ever serves (a handful), not by turns or sessions — and removing a gate that another
    /// delivery is at that moment waiting on is exactly the bug this type is trying not to have.
    /// </summary>
    private readonly ConcurrentDictionary<ConversationId, SemaphoreSlim> _gates = new();

    /// <summary>Creates an adapter that renders turns through <paramref name="client"/>.</summary>
    /// <param name="client">The Bot API transport used to send, edit and show typing.</param>
    /// <param name="clock">The clock the typing-indicator throttle measures against.</param>
    /// <param name="logger">Where dropped deliveries and Telegram failures are reported.</param>
    public TelegramChannelAdapter(
        TelegramBotClient client,
        TimeProvider clock,
        ILogger<TelegramChannelAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _client = client;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ChannelId => ChannelName;

    /// <summary>
    /// How many conversations currently have per-turn state. Test-only hook, the same shape as
    /// <c>ChannelPump.IsTurnRunning</c> and <see cref="TelegramChannelSource.CurrentOffset"/>: releasing the state
    /// on a terminal event is invisible from the outside, because <see cref="StateFor"/>'s turn-id reset would
    /// produce the identical sequence of Bot API calls for the next turn either way. Only a test reading this can
    /// tell "the entry was released" from "the entry was reused", and the difference is whether this dictionary
    /// grows for the lifetime of the process.
    /// </summary>
    internal int TrackedConversations => _turns.Count;

    /// <summary>
    /// Renders <paramref name="agentEvent"/> into the Telegram chat identified by <paramref name="conversationId"/>,
    /// serialised against any other delivery to that same conversation.
    /// Never throws for a delivery failure of any kind — a malformed chat id, a Telegram error or a transport fault
    /// is logged and the delivery is dropped, because the pump calls this from inside a turn and one escaping
    /// exception would take the whole channel down with it.
    /// </summary>
    /// <param name="conversationId">The Telegram chat id, as a string, exactly as <c>TelegramChannelSource</c> supplied it.</param>
    /// <param name="agentEvent">The event to render. Text deltas re-render the turn's message; terminal events render once more and end it.</param>
    /// <param name="ct">A token to cancel the Bot API calls.</param>
    public async ValueTask DeliverAsync(ConversationId conversationId, AgentEvent agentEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);

        var gate = _gates.GetOrAdd(conversationId, static _ => new SemaphoreSlim(1, 1));

        // Tracked separately from the acquisition itself: releasing a semaphore that was never taken (because the
        // wait was cancelled) would hand a second delivery a permit that does not exist.
        var held = false;

        try
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            held = true;
            await DeliverCoreAsync(conversationId, agentEvent, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Deliberately catches everything, OperationCanceledException included: the pump observes cancellation
            // through the runtime's own event stream, and there is no caller above this one that may be allowed to
            // see a throw from a channel adapter. The gate wait is inside this guard for the same reason.
            LogDeliveryFailed(_logger, ex);
        }
        finally
        {
            if (held)
            {
                gate.Release();
            }
        }
    }

    /// <summary>Composes the body this event renders (if any), reads the chat id off the conversation, and publishes it.</summary>
    private async Task DeliverCoreAsync(ConversationId conversationId, AgentEvent agentEvent, CancellationToken ct)
    {
        if (Compose(conversationId, agentEvent) is not { } body)
        {
            // Usage, memory and tool events are the pump's business, not the chat's — rendering them would
            // overwrite the reply the operator is reading.
            return;
        }

        if (!long.TryParse(conversationId.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var chatId))
        {
            // Only reachable if a non-Telegram conversation id is routed here, which would be a pump wiring bug —
            // a drop with a log, never a throw, because this runs inside a turn.
            LogUnparsableChatId(_logger, conversationId.Value);
            return;
        }

        await RenderAsync(conversationId, chatId, agentEvent.TurnId, body, IsTerminal(agentEvent), ct).ConfigureAwait(false);
    }

    /// <summary>The text <paramref name="agentEvent"/> should put on screen, or null when it renders nothing.</summary>
    private string? Compose(ConversationId conversationId, AgentEvent agentEvent) => agentEvent switch
    {
        // The pump coalesces deltas and re-sends the whole accumulated body each time, so Text is the full render.
        TextDeltaEvent delta => delta.Text,

        // The buffered result, not the last delta: the final flush can otherwise be suppressed by the coalescer.
        TurnCompletedEvent completed => completed.Result.Text,

        TurnFailedEvent failed => Failure(conversationId, failed),
        _ => null,
    };

    /// <summary>Whether this event ends the turn, so the final render is followed by clearing the per-turn state.</summary>
    private static bool IsTerminal(AgentEvent agentEvent) => agentEvent is TurnCompletedEvent or TurnFailedEvent;

    /// <summary>
    /// The body a failed turn renders: the error above nothing, or below whatever the turn had already streamed.
    /// Replacing a partial answer with a bare error would throw away output the operator can still use.
    /// </summary>
    private string Failure(ConversationId conversationId, TurnFailedEvent failed)
    {
        // Plain interpolation, not string.Create(InvariantCulture, …): both holes are culture-invariant (an enum
        // name and a string), which is what MA0185 asks for here.
        var notice = $"⚠ The turn failed ({failed.Error.Code}): {failed.Error.Message}";

        return _turns.TryGetValue(conversationId, out var state) && state.TurnId == failed.TurnId && state.Text.Length > 0
            ? state.Text + "\n\n" + notice
            : notice;
    }

    /// <summary>
    /// Publishes one render of <paramref name="turnId"/> into <paramref name="chatId"/>: escape, split, then send or
    /// edit each chunk. A <c>400</c> anywhere in that pass costs the formatting and nothing else — the whole render
    /// is retried once as plain text, which is the only fallback and is never attempted twice.
    /// </summary>
    private async Task RenderAsync(
        ConversationId conversationId, long chatId, TurnId turnId, string body, bool terminal, CancellationToken ct)
    {
        var state = StateFor(conversationId, turnId);

        if (!terminal)
        {
            await KeepTypingAsync(chatId, state, ct).ConfigureAwait(false);
        }

        var chunks = MessageSplitter.Split(MarkdownV2Escaper.Escape(body));
        if (chunks.Count > 0 && !await PublishAsync(chatId, state, chunks, MarkdownV2, ct).ConfigureAwait(false))
        {
            // The escaped body was rejected. Retry with the raw text and no parse mode: losing bold and code
            // fences is a far cheaper failure than losing the answer. Split again, because the escaped body and
            // the raw body do not break at the same offsets.
            var plain = MessageSplitter.Split(body);
            if (plain.Count > 0 && !await PublishAsync(chatId, state, plain, parseMode: null, ct).ConfigureAwait(false))
            {
                LogRenderDropped(_logger);
            }
        }

        state.Text = body;

        if (terminal)
        {
            _turns.TryRemove(conversationId, out _);
        }
    }

    /// <summary>
    /// Sends or edits each chunk in order: chunk <c>i</c> edits the <c>i</c>-th message this turn already owns, or
    /// sends a new one and takes ownership of it. Growing cumulative text therefore edits the messages already on
    /// screen and only ever appends genuinely new ones — re-sending an overflow chunk would duplicate it in the chat.
    /// </summary>
    /// <returns>
    /// True when the whole render went out; false when Telegram refused it with a <c>400</c>, which is the caller's
    /// signal to fall back to plain text. Every other failure propagates to <see cref="DeliverAsync"/>'s guard.
    /// </returns>
    private async Task<bool> PublishAsync(
        long chatId, TurnState state, IReadOnlyList<string> chunks, string? parseMode, CancellationToken ct)
    {
        try
        {
            for (var i = 0; i < chunks.Count; i++)
            {
                if (i < state.MessageIds.Count)
                {
                    // A null return means Telegram considered the text unchanged — nothing to do, not a failure.
                    _ = await _client.EditMessageTextAsync(chatId, state.MessageIds[i], chunks[i], parseMode, ct).ConfigureAwait(false);
                }
                else
                {
                    var sent = await _client.SendMessageAsync(chatId, chunks[i], parseMode, ct).ConfigureAwait(false);
                    state.MessageIds.Add(sent.MessageId);
                }
            }

            return true;
        }
        catch (TelegramApiException ex) when (ex.ErrorCode == 400)
        {
            LogRejected(_logger, parseMode ?? "plain text", ex.Description);
            return false;
        }
    }

    /// <summary>
    /// Refreshes Telegram's "typing…" indicator, at most once every <see cref="TypingInterval"/> per session. It is
    /// decoration: a failure here is logged at debug and the render carries on, because the answer matters and the
    /// indicator does not.
    /// </summary>
    private async Task KeepTypingAsync(long chatId, TurnState state, CancellationToken ct)
    {
        var now = _clock.GetTimestamp();
        if (state.TypingStamp is { } last && _clock.GetElapsedTime(last, now) < TypingInterval)
        {
            return;
        }

        state.TypingStamp = now;

        try
        {
            await _client.SendChatActionAsync(chatId, TypingAction, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogTypingFailed(_logger, ex);
        }
    }

    /// <summary>
    /// The state for this session, reset when <paramref name="turnId"/> is not the turn it was tracking. That reset
    /// is what makes a fresh message appear for a new turn even if the previous one never produced a terminal event
    /// (a cancelled turn, a runtime that ended its stream early) — without it, the next answer would silently
    /// overwrite the last one.
    /// </summary>
    private TurnState StateFor(ConversationId conversationId, TurnId turnId)
    {
        var state = _turns.GetOrAdd(conversationId, static (_, turn) => new TurnState(turn), turnId);
        if (state.TurnId != turnId)
        {
            state.Restart(turnId);
        }

        return state;
    }

    /// <summary>What one conversation's in-flight turn owns in the chat: the messages it renders into and their last body.</summary>
    private sealed class TurnState(TurnId turnId)
    {
        /// <summary>The turn these messages belong to; a different one means the state is stale.</summary>
        public TurnId TurnId { get; private set; } = turnId;

        /// <summary>The messages this turn has sent, in chunk order. Index 0 is the message the turn started with.</summary>
        public List<long> MessageIds { get; } = [];

        /// <summary>The last body rendered, so a failure can be shown underneath the partial answer instead of replacing it.</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>When the typing indicator was last refreshed, as a <see cref="TimeProvider.GetTimestamp"/> reading.</summary>
        public long? TypingStamp { get; set; }

        /// <summary>Drops everything the previous turn owned so <paramref name="turn"/> starts with a fresh message.</summary>
        public void Restart(TurnId turn)
        {
            TurnId = turn;
            MessageIds.Clear();
            Text = string.Empty;
            TypingStamp = null;
        }
    }

    // 710, 712 and 717 were the "cannot resolve a chat from this session" drops. Re-keying DeliverAsync on the
    // conversation deleted the lookup that produced them; the ids are retired rather than reused.
    [LoggerMessage(EventId = 711, Level = LogLevel.Error, Message = "Conversation id {ConversationId} is not a Telegram chat id; the delivery is dropped")]
    private static partial void LogUnparsableChatId(ILogger logger, string conversationId);

    [LoggerMessage(EventId = 713, Level = LogLevel.Error, Message = "Telegram refused the render as plain text too; this render is dropped and the turn continues")]
    private static partial void LogRenderDropped(ILogger logger);

    [LoggerMessage(EventId = 714, Level = LogLevel.Error, Message = "Delivering a turn event to Telegram failed; the delivery is dropped and the channel keeps running")]
    private static partial void LogDeliveryFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 715, Level = LogLevel.Warning, Message = "Telegram rejected the render sent as {ParseMode}: {Description}")]
    private static partial void LogRejected(ILogger logger, string parseMode, string? description);

    [LoggerMessage(EventId = 716, Level = LogLevel.Debug, Message = "Refreshing the typing indicator failed; the render is unaffected")]
    private static partial void LogTypingFailed(ILogger logger, Exception ex);
}
