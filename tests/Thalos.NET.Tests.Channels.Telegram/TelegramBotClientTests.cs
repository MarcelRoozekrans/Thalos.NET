using Thalos.Channels.Telegram;
using Thalos.Tests.Channels.Telegram.Fakes;

namespace Thalos.Tests.Channels.Telegram;

public sealed class TelegramBotClientTests
{
    private static TelegramBotClient Build(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/") }, "TOKEN");

    [Fact]
    public async Task The_token_is_in_the_path_and_never_in_a_query_string()
    {
        var handler = new StubHandler(StubHandler.Json("""{"ok":true,"result":[]}"""));
        await Build(handler).GetUpdatesAsync(0, 50, default);

        handler.Requests[0].Should().StartWith("/botTOKEN/getUpdates");
    }

    [Fact]
    public async Task GetUpdates_returns_the_parsed_updates()
    {
        var handler = new StubHandler(StubHandler.Json("""
        {"ok":true,"result":[{"update_id":9,"message":{"message_id":1,"text":"hi","chat":{"id":42,"type":"private"}}}]}
        """));

        var updates = await Build(handler).GetUpdatesAsync(0, 50, default);
        updates.Should().ContainSingle().Which.UpdateId.Should().Be(9);
    }

    [Fact]
    public async Task SendMessage_returns_the_parsed_message_on_success()
    {
        // Task 17 sends a message, keeps the returned message_id, and edits that message in place to stream the
        // reply — this is the exact shape it depends on: a non-zero id and the text round-tripping correctly.
        var handler = new StubHandler(StubHandler.Json("""
        {"ok":true,"result":{"message_id":123,"text":"hello there","chat":{"id":42,"type":"private"}}}
        """));

        var message = await Build(handler).SendMessageAsync(42, "hello there", null, default);

        message.MessageId.Should().Be(123);
        message.Text.Should().Be("hello there");
    }

    [Fact]
    public async Task A_429_throws_with_retry_after_so_the_caller_can_honour_it()
    {
        var handler = new StubHandler(StubHandler.Json(
            """{"ok":false,"error_code":429,"description":"Too Many Requests","parameters":{"retry_after":7}}""",
            System.Net.HttpStatusCode.TooManyRequests));

        var act = async () => await Build(handler).SendMessageAsync(42, "hi", null, default);

        (await act.Should().ThrowAsync<TelegramApiException>())
            .Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task A_400_parse_failure_throws_with_the_error_code_so_the_adapter_can_retry_as_plain_text()
    {
        var handler = new StubHandler(StubHandler.Json(
            """{"ok":false,"error_code":400,"description":"Bad Request: can't parse entities"}""",
            System.Net.HttpStatusCode.BadRequest));

        var act = async () => await Build(handler).SendMessageAsync(42, "*broken", "MarkdownV2", default);

        (await act.Should().ThrowAsync<TelegramApiException>())
            .Which.ErrorCode.Should().Be(400);
    }

    [Fact]
    public async Task A_400_not_modified_is_swallowed_because_an_unchanged_edit_is_not_a_failure()
    {
        var handler = new StubHandler(StubHandler.Json(
            """{"ok":false,"error_code":400,"description":"Bad Request: message is not modified"}""",
            System.Net.HttpStatusCode.BadRequest));

        var act = async () => await Build(handler).EditMessageTextAsync(42, 1, "same", null, default);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_400_not_modified_still_throws_for_send_message_because_there_nothing_was_sent_to_no_op()
    {
        // The "message is not modified" carve-out only makes sense for an edit of an existing message; sendMessage never has an
        // existing message to compare against, so the same description here is a real failure and must not be swallowed.
        var handler = new StubHandler(StubHandler.Json(
            """{"ok":false,"error_code":400,"description":"Bad Request: message is not modified"}""",
            System.Net.HttpStatusCode.BadRequest));

        var act = async () => await Build(handler).SendMessageAsync(42, "hi", null, default);
        await act.Should().ThrowAsync<TelegramApiException>();
    }

    [Fact]
    public async Task The_token_never_appears_in_the_exception_message()
    {
        // The token lives in the request path. If a future change ever folded the request URI (or any header) into the
        // exception message, this would catch it before it reached a log aggregator.
        var handler = new StubHandler(StubHandler.Json(
            """{"ok":false,"error_code":403,"description":"Forbidden: bot was blocked by the user"}""",
            System.Net.HttpStatusCode.Forbidden));

        var act = async () => await Build(handler).SendMessageAsync(42, "hi", null, default);

        (await act.Should().ThrowAsync<TelegramApiException>())
            .Which.Message.Should().NotContain("TOKEN");
    }
}
