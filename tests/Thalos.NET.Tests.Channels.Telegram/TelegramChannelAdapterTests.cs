using System.Globalization;
using System.Net;
using Thalos.Channels;
using Thalos.Channels.Telegram;
using Thalos.Tests.Channels.Telegram.Fakes;

namespace Thalos.Tests.Channels.Telegram;

public sealed class TelegramChannelAdapterTests
{
    /// <summary>The conversation id the pump hands the adapter IS the Telegram chat id. No lookup, no binding.</summary>
    private static readonly ConversationId Chat = new("42");

    private static readonly ConversationId OtherChat = new("99");

    /// <summary>A session for the events to carry. Nothing routes on it any more; the adapter never reads it.</summary>
    private static readonly SessionId Session = SessionId.New();

    /// <summary>Everything one adapter test needs: the adapter and the wire it talks over.</summary>
    private sealed record Harness(TelegramChannelAdapter Adapter, StubHandler Handler, CapturingLogger<TelegramChannelAdapter> Log)
    {
        /// <summary>The Bot API method of every request made so far, in order (request bodies filtered out).</summary>
        public List<string> Methods =>
            [.. Handler.Requests.Where(r => r.StartsWith("/bot", StringComparison.Ordinal)).Select(r => r[(r.LastIndexOf('/') + 1)..])];

        /// <summary>The request bodies, in order, positionally aligned with <see cref="Methods"/>.</summary>
        public List<string> Bodies =>
            [.. Handler.Requests.Where(r => !r.StartsWith("/bot", StringComparison.Ordinal))];
    }

    private static Harness Build(params HttpResponseMessage[] responses)
    {
        var handler = new StubHandler(responses);
        var client = new TelegramBotClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/") }, "T");

        var log = new CapturingLogger<TelegramChannelAdapter>();
        return new Harness(new TelegramChannelAdapter(client, TimeProvider.System, log), handler, log);
    }

    /// <summary>
    /// A successful <c>sendMessage</c>/<c>editMessageText</c> response carrying <paramref name="messageId"/>. Not a raw
    /// interpolated literal: the run of closing braces at the tail is longer than a <c>$$</c> literal can disambiguate,
    /// so this uses an ordinary interpolated string with every literal brace doubled (as the source tests do).
    /// </summary>
    private static HttpResponseMessage Message(long messageId) => StubHandler.Json(string.Create(
        CultureInfo.InvariantCulture,
        $"{{\"ok\":true,\"result\":{{\"message_id\":{messageId},\"text\":\"x\",\"chat\":{{\"id\":42,\"type\":\"private\"}}}}}}"));

    /// <summary>A successful <c>sendChatAction</c> response.</summary>
    private static HttpResponseMessage Acted() => StubHandler.Json("""{"ok":true,"result":true}""");

    /// <summary>Telegram refusing a call with a 400 — what a MarkdownV2 parse failure looks like on the wire.</summary>
    private static HttpResponseMessage ParseFailure() => StubHandler.Json(
        """{"ok":false,"error_code":400,"description":"Bad Request: can't parse entities: Character '.' is reserved"}""",
        HttpStatusCode.BadRequest);

    /// <summary>The 400 the client deliberately swallows for edits: an edit that changes nothing.</summary>
    private static HttpResponseMessage NotModified() => StubHandler.Json(
        """{"ok":false,"error_code":400,"description":"Bad Request: message is not modified"}""",
        HttpStatusCode.BadRequest);

    private static TextDeltaEvent Delta(TurnId turn, string text) => new(Session, turn, text);

    private static TurnCompletedEvent Completed(TurnId turn, string text) =>
        new(Session, turn, new AgentTurnResult(turn, Session, text, default, [], TimeSpan.Zero));

    private static TurnFailedEvent Failed(TurnId turn) =>
        new(Session, turn, new AgentError(AgentErrorCode.ProviderError, "the model hung up"));

    [Fact]
    public void The_channel_id_is_telegram()
    {
        Build().Adapter.ChannelId.Should().Be("telegram");
    }

    [Fact]
    public async Task A_notice_for_a_conversation_with_no_session_is_delivered_rather_than_dropped()
    {
        // THE REGRESSION TEST. /help, /agents, the busy notice and "that session had already ended" are all sent by
        // the pump with NO session — there is nothing bound to look up. While DeliverAsync was keyed on SessionId,
        // the adapter had to resolve a chat from that id, found nothing, and dropped the notice: on Telegram every
        // one of those commands produced silence. Keyed on the conversation there is nothing to resolve.
        var h = Build(Acted(), Message(1));

        await h.Adapter.DeliverAsync(Chat, new TextDeltaEvent(default, TurnId.New(), ChannelNotices.Help), CancellationToken.None);

        h.Methods.Should().Equal("sendChatAction", "sendMessage");
        h.Bodies[1].Should().Contain("\"chat_id\":42").And.Contain("/agents");
        h.Log.EventIds.Should().BeEmpty();
    }

    [Fact]
    public async Task The_first_render_sends_and_later_renders_edit_that_same_message()
    {
        var h = Build(Acted(), Message(555), Message(555));
        var turn = TurnId.New();

        // Cumulative renders: the second delta carries the whole text so far, not just the new fragment.
        await h.Adapter.DeliverAsync(Chat, Delta(turn, "Hel"), CancellationToken.None);
        await h.Adapter.DeliverAsync(Chat, Delta(turn, "Hello there"), CancellationToken.None);

        h.Methods.Should().Equal("sendChatAction", "sendMessage", "editMessageText");

        // The edit must target the id sendMessage returned — not a second send, and not message 0.
        h.Bodies[1].Should().Contain("\"parse_mode\":\"MarkdownV2\"").And.NotContain("message_id");
        h.Bodies[2].Should().Contain("\"message_id\":555").And.Contain("Hello there");
    }

    [Fact]
    public async Task The_typing_indicator_is_not_sent_once_per_delta()
    {
        var h = Build(Acted(), Message(1), Message(1), Message(1));
        var turn = TurnId.New();

        await h.Adapter.DeliverAsync(Chat, Delta(turn, "a"), CancellationToken.None);
        await h.Adapter.DeliverAsync(Chat, Delta(turn, "ab"), CancellationToken.None);
        await h.Adapter.DeliverAsync(Chat, Delta(turn, "abc"), CancellationToken.None);

        // Three renders of one turn, one typing action: the 4s throttle cannot elapse inside a test.
        h.Methods.Count(m => string.Equals(m, "sendChatAction", StringComparison.Ordinal)).Should().Be(1);
        h.Methods.Count(m => string.Equals(m, "editMessageText", StringComparison.Ordinal)).Should().Be(2);
    }

    [Fact]
    public async Task A_400_on_the_markdown_send_is_retried_once_with_the_unescaped_text()
    {
        var h = Build(Acted(), ParseFailure(), Message(9));

        await h.Adapter.DeliverAsync(Chat, Delta(TurnId.New(), "Ready (v1.0)."), CancellationToken.None);

        h.Methods.Should().Equal("sendChatAction", "sendMessage", "sendMessage");

        // Attempt 1: MarkdownV2 over escaped text — each reserved character carries a backslash, which JSON
        // then doubles. Attempt 2: the ORIGINAL text with no parse_mode at all (the JSON context omits nulls),
        // so the answer survives even though the formatting does not.
        h.Bodies[1].Should().Contain(@"\\(v1\\.0\\)").And.Contain("\"parse_mode\":\"MarkdownV2\"");
        h.Bodies[2].Should().Contain(@"""text"":""Ready (v1.0).""").And.NotContain("parse_mode");
    }

    [Fact]
    public async Task A_400_on_the_plain_text_retry_drops_the_render_without_throwing_or_retrying_again()
    {
        // The fourth response would succeed: a third attempt would therefore be visible as a third sendMessage
        // rather than as some unrelated failure.
        var h = Build(Acted(), ParseFailure(), ParseFailure(), Message(9));

        var act = async () => await h.Adapter.DeliverAsync(Chat, Delta(TurnId.New(), "boom."), CancellationToken.None);

        await act.Should().NotThrowAsync();
        h.Methods.Should().Equal("sendChatAction", "sendMessage", "sendMessage");
        h.Log.EventIds.Should().Contain(713);
    }

    [Fact]
    public async Task A_completed_turn_edits_once_more_and_the_next_turn_sends_a_fresh_message()
    {
        var h = Build(Acted(), Message(100), Message(100), Acted(), Message(200));
        var first = TurnId.New();
        var second = TurnId.New();

        await h.Adapter.DeliverAsync(Chat, Delta(first, "partial"), CancellationToken.None);
        await h.Adapter.DeliverAsync(Chat, Completed(first, "partial answer"), CancellationToken.None);
        await h.Adapter.DeliverAsync(Chat, Delta(second, "a new question"), CancellationToken.None);

        h.Methods.Should().Equal("sendChatAction", "sendMessage", "editMessageText", "sendChatAction", "sendMessage");

        // The completed turn edits message 100 with the buffered final text...
        h.Bodies[2].Should().Contain("\"message_id\":100").And.Contain("partial answer");

        // ...and the next turn must not touch 100 again: the state was cleared, so this is a fresh send.
        h.Bodies[4].Should().Contain("a new question").And.NotContain("message_id");
    }

    [Fact]
    public async Task A_new_turn_that_follows_no_terminal_event_still_sends_a_fresh_message()
    {
        // A cancelled turn ends without a terminal event, so nothing clears the state — the turn id changing is
        // what has to. Without that, the next answer would silently overwrite the previous one in the chat.
        var h = Build(Acted(), Message(1), Acted(), Message(2));

        await h.Adapter.DeliverAsync(Chat, Delta(TurnId.New(), "abandoned half-answer"), CancellationToken.None);
        await h.Adapter.DeliverAsync(Chat, Delta(TurnId.New(), "the next question"), CancellationToken.None);

        h.Methods.Should().Equal("sendChatAction", "sendMessage", "sendChatAction", "sendMessage");
        h.Bodies[3].Should().Contain("the next question").And.NotContain("message_id");
    }

    [Fact]
    public async Task A_terminal_event_releases_the_conversation_state_instead_of_holding_it_for_the_process_lifetime()
    {
        // Not observable through the Bot API calls (the turn-id reset above would produce the same ones), so this
        // reads the internal counter: without the release, one entry accumulates per conversation the host serves.
        var h = Build(Acted(), Message(3), Message(3));
        var turn = TurnId.New();

        await h.Adapter.DeliverAsync(Chat, Delta(turn, "working"), CancellationToken.None);
        h.Adapter.TrackedConversations.Should().Be(1);

        await h.Adapter.DeliverAsync(Chat, Completed(turn, "done"), CancellationToken.None);
        h.Adapter.TrackedConversations.Should().Be(0);
    }

    [Fact]
    public async Task A_body_longer_than_the_cap_sends_the_overflow_as_extra_messages_and_edits_them_afterwards()
    {
        var h = Build(Acted(), Message(1), Message(2), NotModified(), Message(2));
        var turn = TurnId.New();

        // No MarkdownV2-reserved character in the body, so escaping is a no-op and the lengths below are exact.
        var first = new string('a', 4096) + new string('b', 400);
        var second = first + new string('c', 100);

        await h.Adapter.DeliverAsync(Chat, Delta(turn, first), CancellationToken.None);
        await h.Adapter.DeliverAsync(Chat, Delta(turn, second), CancellationToken.None);

        h.Methods.Should().Equal("sendChatAction", "sendMessage", "sendMessage", "editMessageText", "editMessageText");

        // First render: chunk 0 is exactly the 4096-character cap, chunk 1 is the overflow — a second message,
        // not a truncation. ("aa" and "b" appear nowhere in the surrounding JSON envelope.)
        h.Bodies[1].Should().Contain(new string('a', 4096)).And.NotContain("b");
        h.Bodies[2].Should().Contain(new string('b', 400)).And.NotContain("aa");

        // Second render: BOTH chunks are edited by id. Re-sending the overflow would duplicate it in the chat.
        h.Bodies[3].Should().Contain("\"message_id\":1");
        h.Bodies[4].Should().Contain("\"message_id\":2").And.Contain(new string('c', 100));
    }

    [Fact]
    public async Task A_failed_turn_renders_the_error_code()
    {
        var h = Build(Message(1));

        await h.Adapter.DeliverAsync(Chat, Failed(TurnId.New()), CancellationToken.None);

        // Terminal: no typing action, and nothing was tracked yet, so the failure is its own new message.
        h.Methods.Should().Equal("sendMessage");
        h.Bodies[0].Should().Contain("ProviderError").And.Contain("the model hung up");
    }

    [Fact]
    public async Task A_failed_turn_keeps_the_partial_answer_above_the_error()
    {
        var h = Build(Acted(), Message(77), Message(77));
        var turn = TurnId.New();

        await h.Adapter.DeliverAsync(Chat, Delta(turn, "as far as I got"), CancellationToken.None);
        await h.Adapter.DeliverAsync(Chat, Failed(turn), CancellationToken.None);

        h.Methods.Should().Equal("sendChatAction", "sendMessage", "editMessageText");
        h.Bodies[2].Should().Contain("as far as I got").And.Contain("ProviderError");
    }

    [Fact]
    public async Task A_failed_turn_also_clears_the_state_so_the_next_turn_sends_fresh()
    {
        var h = Build(Acted(), Message(77), Message(77), Acted(), Message(88));
        var first = TurnId.New();

        await h.Adapter.DeliverAsync(Chat, Delta(first, "half"), CancellationToken.None);
        await h.Adapter.DeliverAsync(Chat, Failed(first), CancellationToken.None);
        await h.Adapter.DeliverAsync(Chat, Delta(TurnId.New(), "try again"), CancellationToken.None);

        h.Methods.Should().Equal("sendChatAction", "sendMessage", "editMessageText", "sendChatAction", "sendMessage");
        h.Bodies[4].Should().Contain("try again").And.NotContain("message_id");
    }

    [Fact]
    public async Task A_conversation_id_that_is_not_a_chat_id_is_dropped_rather_than_parsed()
    {
        var h = Build();

        var act = async () => await h.Adapter.DeliverAsync(
            new ConversationId("stdin"), Delta(TurnId.New(), "hi"), CancellationToken.None);

        await act.Should().NotThrowAsync();
        h.Methods.Should().BeEmpty();
        h.Log.EventIds.Should().Contain(711);
    }

    [Fact]
    public async Task A_transport_failure_is_logged_and_never_leaves_DeliverAsync()
    {
        // An unparsable body: the client throws while deserializing, which is neither a TelegramApiException nor
        // anything the render path anticipates — exactly the shape that has killed a channel three times here.
        var h = Build(Acted(), StubHandler.Json("<html>502 Bad Gateway</html>"));

        var act = async () => await h.Adapter.DeliverAsync(Chat, Delta(TurnId.New(), "hi"), CancellationToken.None);

        await act.Should().NotThrowAsync();
        h.Log.EventIds.Should().Contain(714);
    }

    [Fact]
    public async Task A_failing_typing_indicator_never_stops_the_render()
    {
        // sendChatAction is decoration; a 403 on it must not cost the operator the actual answer.
        var h = Build(
            StubHandler.Json("""{"ok":false,"error_code":403,"description":"Forbidden"}""", HttpStatusCode.Forbidden),
            Message(5));

        await h.Adapter.DeliverAsync(Chat, Delta(TurnId.New(), "still delivered"), CancellationToken.None);

        h.Methods.Should().Equal("sendChatAction", "sendMessage");
        h.Bodies[1].Should().Contain("still delivered");
    }

    [Fact]
    public async Task An_empty_render_sends_nothing_rather_than_an_empty_message()
    {
        var h = Build();

        await h.Adapter.DeliverAsync(Chat, Completed(TurnId.New(), string.Empty), CancellationToken.None);

        // Telegram rejects an empty text outright; MessageSplitter yields no chunks, so there is nothing to send.
        h.Methods.Should().BeEmpty();
    }

    [Fact]
    public async Task An_event_the_adapter_does_not_render_costs_nothing()
    {
        var h = Build();

        await h.Adapter.DeliverAsync(Chat, new UsageEvent(Session, TurnId.New(), default), CancellationToken.None);

        h.Methods.Should().BeEmpty();
        h.Log.EventIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Two_conversations_are_tracked_independently()
    {
        var h = Build(Acted(), Message(11), Acted(), Message(22), Message(11));
        var mine = TurnId.New();

        await h.Adapter.DeliverAsync(Chat, Delta(mine, "first chat"), CancellationToken.None);
        await h.Adapter.DeliverAsync(OtherChat, Delta(TurnId.New(), "second chat"), CancellationToken.None);
        await h.Adapter.DeliverAsync(Chat, Delta(mine, "first chat again"), CancellationToken.None);

        // The interleaved second conversation must not clobber the first one's tracked message or its chat.
        h.Methods.Should().Equal("sendChatAction", "sendMessage", "sendChatAction", "sendMessage", "editMessageText");
        h.Bodies[1].Should().Contain("\"chat_id\":42");
        h.Bodies[3].Should().Contain("\"chat_id\":99");
        h.Bodies[4].Should().Contain("\"chat_id\":42").And.Contain("\"message_id\":11").And.Contain("first chat again");
    }

    [Fact]
    public async Task A_notice_racing_an_in_flight_turn_on_the_same_conversation_costs_neither_message()
    {
        // The pump sends notices from its reader loop while a turn streams on a detached task, so these two
        // genuinely overlap on one conversation. Unsynchronised, the notice's Restart() clears MessageIds while
        // the turn is parked mid-publish between chunk 0 and chunk 1 — and the turn's render is corrupted or, if
        // the bounds check and the indexer straddle the clear, dropped outright by the catch-all.
        var h = Build(Acted(), Message(1), Message(2), Message(1), Message(2), Acted(), Message(3), Message(4));
        var turn = TurnId.New();

        // Two chunks, so the publish loop has an await in the middle of it to be interrupted in.
        var first = new string('a', 4096) + new string('b', 400);
        var second = first + new string('c', 100);

        await h.Adapter.DeliverAsync(Chat, Delta(turn, first), CancellationToken.None);

        // Hold the turn inside the edit of its FIRST chunk; the notice is issued while that call is outstanding.
        var (reached, release) = h.Handler.BlockNextRequestContaining("\"message_id\":1");
        var rendering = h.Adapter.DeliverAsync(Chat, Delta(turn, second), CancellationToken.None).AsTask();

        try
        {
            // Timed out rather than awaited bare: if the marker ever stops matching (a body format change),
            // the handler never blocks, "reached" never completes, and a bare await would hang the run forever
            // instead of failing it.
            await reached.WaitAsync(TimeSpan.FromSeconds(10));
            var notice = h.Adapter.DeliverAsync(
                Chat, new TextDeltaEvent(default, TurnId.New(), ChannelNotices.Busy), CancellationToken.None).AsTask();

            release.SetResult();
            await rendering;
            await notice;
        }
        finally
        {
            release.TrySetResult();
        }

        // The turn ends AFTER the notice interrupted it; its final answer must still arrive.
        await h.Adapter.DeliverAsync(Chat, Completed(turn, "the final answer"), CancellationToken.None);

        // Strictly serialised: the turn finishes editing both of its chunks before the notice sends anything.
        h.Methods.Should().Equal(
            "sendChatAction", "sendMessage", "sendMessage",
            "editMessageText", "editMessageText",
            "sendChatAction", "sendMessage",
            "sendMessage");

        h.Bodies[6].Should().Contain(ChannelNotices.Busy[..20]);
        h.Bodies[7].Should().Contain("the final answer");

        // Nothing was dropped: every drop path in this adapter logs, so an empty log is the assertion.
        h.Log.EventIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Two_conversations_deliver_concurrently_so_the_gate_is_not_global()
    {
        var h = Build(Acted(), Message(1), Acted(), Message(2));

        // Chat 42 is held inside sendMessage for the whole of chat 99's delivery. The marker is the message text,
        // not the chat id — the chat id also appears in that conversation's sendChatAction body.
        var (reached, release) = h.Handler.BlockNextRequestContaining("slow chat");
        var blocked = h.Adapter.DeliverAsync(Chat, Delta(TurnId.New(), "slow chat"), CancellationToken.None).AsTask();

        try
        {
            // Same reason as the test above: a marker that stops matching must fail, not hang.
            await reached.WaitAsync(TimeSpan.FromSeconds(10));

            // A global gate would park this until chat 42 completes, which it cannot do until we release it —
            // the WaitAsync turns that deadlock into a timeout failure rather than a hung test run.
            await h.Adapter.DeliverAsync(OtherChat, Delta(TurnId.New(), "fast chat"), CancellationToken.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.TrySetResult();
        }

        await blocked;

        // Every delivery has been awaited by now, so nothing is still writing to the handler.
        {
            h.Bodies.Should().Contain(b => b.Contains("\"chat_id\":99", StringComparison.Ordinal) && b.Contains("fast chat", StringComparison.Ordinal));
            h.Bodies.Should().Contain(b => b.Contains("\"chat_id\":42", StringComparison.Ordinal) && b.Contains("slow chat", StringComparison.Ordinal));
        }

        h.Log.EventIds.Should().BeEmpty();
    }
}
