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

    private static (TelegramChannelSource Source, CapturingLogger<TelegramChannelSource> Logger) BuildWithCapturingLogger(
        StubHandler handler, params long[] allowed)
    {
        var client = new TelegramBotClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/") }, "T");

        var logger = new CapturingLogger<TelegramChannelSource>();
        var source = new TelegramChannelSource(client, new TelegramOptions
        {
            BotToken = "T",
            AllowedUserIds = [.. allowed],
            PrincipalId = "telegram:test",
            Roles = [],
        }, logger);

        return (source, logger);
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

        // This only shows the offset advanced to 12 by the time request 2 was sent — NOT that the ack happened
        // before message 1 was yielded rather than after. Both orderings send offset=12 on request 2 (an
        // ack-after-yield implementation resumes at the yield, advances the offset, THEN polls again), so this
        // assertion cannot tell them apart; see the ordering test immediately below for the assertion that can.
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
    public async Task A_malformed_batch_is_skipped_by_its_own_guards_not_by_the_backstop_catch()
    {
        // First batch: a null array element (Telegram sending garbage) and an update whose message has no "chat"
        // at all — both are shapes System.Text.Json will happily produce despite TelegramMessage.Chat and
        // TelegramChat.Type being annotated non-nullable; nothing enforces that at runtime.
        //
        // The loop surviving and later delivering a valid message is NOT, by itself, proof that the individual
        // null guards fired: ProcessBatch's defensive backstop catch (event 707) would produce the exact same
        // observable outcome — this batch skipped, later messages still delivered — if either guard were removed
        // by a future regression, since the resulting NullReferenceException would just be caught there instead.
        // Only the logged event ids can tell "dropped by its own guard" apart from "blew up and got caught".
        //
        // Draining far past what this scenario can legitimately produce (only two valid updates are queued; every
        // request after that gets StubHandler's default empty response) also makes the message-count assertion
        // meaningful: the run exhausts on the CTS timeout rather than stopping the instant *anything* arrives, so
        // an extra, spurious message slipping out of the malformed batch would change the asserted count.
        const string malformedBatch =
            """{"ok":true,"result":[null,{"update_id":5,"message":{"message_id":9,"text":"hi","from":{"id":7,"is_bot":false}}}]}""";

        var handler = new StubHandler(
            StubHandler.Json(malformedBatch),
            StubHandler.Json(Update(6, userId: 7)),
            StubHandler.Json(Update(7, userId: 7)));

        var (source, logger) = BuildWithCapturingLogger(handler, allowed: 7);
        var messages = await Drain(source, expected: 100);

        messages.Should().HaveCount(2);
        messages[0].ExternalMessageId.Should().Be("1");
        messages[1].ExternalMessageId.Should().Be("1");

        logger.EventIds.Should().NotContain(707); // the backstop catch never fired
        logger.EventIds.Should().Contain(706); // the null array element was dropped by its own guard
        logger.EventIds.Should().Contain(704); // the chat-less update was dropped by the private-chat gate
    }

    [Fact]
    public async Task A_second_enumeration_of_the_same_source_is_rejected()
    {
        var source = Build(new StubHandler(StubHandler.Json(Update(1, userId: 7))), allowed: 7);
        await Drain(source, 1);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var secondEnumeration = async () =>
        {
            await foreach (var _ in source.ReadAsync(cts.Token))
            {
                break;
            }
        };

        await secondEnumeration.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_batch_that_never_advances_the_offset_is_rate_limited_not_spun()
    {
        // {"result":[null]} is non-empty (Count == 1) but AdvanceOffset has nothing to advance past — this is
        // exactly the shape Finding E (round 2 of this task's review) fixed: applying MinPollInterval only when the
        // batch was EMPTY left a non-empty, non-advancing batch free to re-poll at zero delay. Fed this shape
        // forever, a source without that fix would pin a core for as long as the channel stays up. This is the
        // only fixture in the suite that is non-empty yet never advances the offset — the malformed-batch test
        // above advances past update 5, so it cannot exercise this branch.
        var responses = Enumerable.Range(0, 60)
            .Select(_ => StubHandler.Json("""{"ok":true,"result":[null]}"""))
            .ToArray();
        var handler = new StubHandler(responses);
        var source = Build(handler, allowed: 7);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await foreach (var _ in source.ReadAsync(cts.Token))
            {
                // No message can ever arrive from this fixture; enumerating just runs the loop out the clock so
                // the request count below reflects how many polls actually happened in the window.
            }
        }
        catch (OperationCanceledException)
        {
        }

        var pollCount = handler.Requests.Count(r => r.StartsWith("/botT/getUpdates", StringComparison.Ordinal));

        // ~10 polls are expected over 2s at the 200ms floor. 50 is a generous ceiling for a loaded machine, yet
        // still fails hard against a zero-delay spin, which would issue orders of magnitude more requests in the
        // same window.
        pollCount.Should().BeLessThanOrEqualTo(50);
    }
}
