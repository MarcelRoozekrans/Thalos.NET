using System.Globalization;
using Thalos.Channels.Telegram;
using Thalos.Tests.Channels.Telegram.Fakes;

namespace Thalos.Tests.Channels.Telegram;

public sealed class TelegramChannelSourceTests
{
    private static TelegramChannelSource Build(StubHandler handler, params long[] allowed)
    {
        // Two-arg constructor: the client is a pure transport and owns no timing (see Task 13).
        var client = new TelegramBotClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/") }, "T");

        return new TelegramChannelSource(client, new TelegramOptions
        {
            BotToken = "T",
            AllowedUserIds = [.. allowed],
            PrincipalId = "telegram:test",
            Roles = [],
        }, Microsoft.Extensions.Logging.Abstractions.NullLogger<TelegramChannelSource>.Instance);
    }

    // Not a raw interpolated string literal: the JSON below has a run of consecutive closing braces (see the tail,
    // after "is_bot":false) too long for a $$ raw literal to disambiguate without an unreadable pile of $ signs, so
    // this uses an ordinary interpolated string with every literal brace doubled instead.
    private static string Update(long updateId, long userId, string chatType = "private", string? text = "hi")
    {
        var textField = text is null ? string.Empty : $"\"text\":\"{text}\",";

        return string.Create(CultureInfo.InvariantCulture,
            $"{{\"ok\":true,\"result\":[{{\"update_id\":{updateId},\"message\":{{\"message_id\":1,{textField}\"chat\":{{\"id\":42,\"type\":\"{chatType}\"}},\"from\":{{\"id\":{userId},\"is_bot\":false}}}}}}]}}");
    }

    private static async Task<List<InboundMessage>> Drain(TelegramChannelSource source, int expected)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var messages = new List<InboundMessage>();
        try
        {
            await foreach (var m in source.ReadAsync(cts.Token))
            {
                messages.Add(m);
                if (messages.Count >= expected)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        return messages;
    }

    [Fact]
    public async Task An_allow_listed_private_message_becomes_an_inbound_message()
    {
        var source = Build(new StubHandler(StubHandler.Json(Update(1, userId: 7))), allowed: 7);
        var messages = await Drain(source, 1);

        messages.Should().ContainSingle();
        messages[0].ChannelId.Should().Be("telegram");
        messages[0].ConversationId.Value.Should().Be("42");
        messages[0].Caller.Id.Should().Be("telegram:test");
    }

    [Fact]
    public async Task A_user_who_is_not_allow_listed_is_dropped_and_never_answered()
    {
        var source = Build(new StubHandler(StubHandler.Json(Update(1, userId: 999))), allowed: 7);
        (await Drain(source, 1)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_group_chat_is_dropped_even_when_the_sender_is_allow_listed()
    {
        var source = Build(new StubHandler(StubHandler.Json(Update(1, userId: 7, chatType: "group"))), allowed: 7);
        (await Drain(source, 1)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_message_with_no_text_is_skipped_rather_than_crashing_the_loop()
    {
        var source = Build(new StubHandler(StubHandler.Json(Update(1, userId: 7, text: null))), allowed: 7);
        (await Drain(source, 1)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_offset_advances_past_the_highest_update_seen()
    {
        // Two real batches, each with one allow-listed message: draining both is what actually forces a second
        // getUpdates call to happen (a single MoveNextAsync only ever drives the first poll). The client POSTs a
        // JSON body rather than a query string, so the second request's body is asserted directly rather than a
        // "offset=" query fragment.
        var handler = new StubHandler(
            StubHandler.Json(Update(11, userId: 7)),
            StubHandler.Json(Update(20, userId: 7)));

        var source = Build(handler, allowed: 7);
        var messages = await Drain(source, 2);

        messages.Should().HaveCount(2);

        // The second getUpdates must ask for 12 — proving the ack for the first batch happened before that
        // batch's message was yielded and processed, not after.
        handler.Requests.Should().Contain(r => r.Contains("\"offset\":12", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_offset_advances_before_the_message_is_yielded_not_after()
    {
        // The request-body assertion above cannot, by itself, distinguish ack-before-yield from ack-after-yield:
        // an ack-after-yield implementation resumes at the yield, advances the offset, THEN polls again — so
        // request 2 would carry offset 12 either way. The only observable difference between the two orderings is
        // whether the offset has already moved by the time the FIRST message reaches the consumer, before a
        // second poll is even requested — which is exactly what this reads via the internal test hook.
        var handler = new StubHandler(StubHandler.Json(Update(11, userId: 7)));
        var source = Build(handler, allowed: 7);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var enumerator = source.ReadAsync(cts.Token).GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).Should().BeTrue();

        source.CurrentOffset.Should().Be(12);
    }

    [Fact]
    public async Task A_malformed_batch_is_skipped_and_the_loop_survives_to_deliver_the_next_valid_message()
    {
        // First batch: a null array element (Telegram sending garbage) and an update whose message has no "chat"
        // at all — both are shapes System.Text.Json will happily produce despite TelegramMessage.Chat and
        // TelegramChat.Type being annotated non-nullable; nothing enforces that at runtime. Neither may crash
        // ReadAsync — both must be skipped like any other admission-gate failure, and the channel must go on to
        // deliver the next real message rather than dying with it.
        const string malformedBatch =
            """{"ok":true,"result":[null,{"update_id":5,"message":{"message_id":9,"text":"hi","from":{"id":7,"is_bot":false}}}]}""";

        var handler = new StubHandler(
            StubHandler.Json(malformedBatch),
            StubHandler.Json(Update(6, userId: 7)));

        var source = Build(handler, allowed: 7);
        var messages = await Drain(source, 1);

        messages.Should().ContainSingle();
        messages[0].ConversationId.Value.Should().Be("42");
        messages[0].Text.Should().Be("hi");
    }
}
