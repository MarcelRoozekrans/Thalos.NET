using System.Text.Json;
using Thalos.Channels.Telegram;

namespace Thalos.Tests.Channels.Telegram;

public sealed class TelegramDtoTests
{
    [Fact]
    public void An_ok_response_deserializes_with_the_source_generated_context()
    {
        const string json = """
        {"ok":true,"result":[{"update_id":11,"message":{"message_id":5,"text":"hi",
        "chat":{"id":42,"type":"private"},"from":{"id":7,"is_bot":false}}}]}
        """;

        var response = JsonSerializer.Deserialize(json, TelegramJsonContext.Default.TelegramResponseUpdateArray);

        response!.Ok.Should().BeTrue();
        response.Result.Should().ContainSingle();
        var update = response.Result![0];
        update.UpdateId.Should().Be(11);
        update.Message!.Text.Should().Be("hi");
        update.Message.Chat.Id.Should().Be(42);
        update.Message.Chat.Type.Should().Be("private");
        update.Message.From!.Id.Should().Be(7);
    }

    [Fact]
    public void A_429_response_exposes_retry_after()
    {
        const string json = """
        {"ok":false,"error_code":429,"description":"Too Many Requests","parameters":{"retry_after":7}}
        """;

        var response = JsonSerializer.Deserialize(json, TelegramJsonContext.Default.TelegramResponseUpdateArray);

        response!.Ok.Should().BeFalse();
        response.ErrorCode.Should().Be(429);
        response.Parameters!.RetryAfter.Should().Be(7);
    }

    [Fact]
    public void A_message_without_text_deserializes_with_a_null_text()
    {
        // Photos, stickers and joins all arrive as messages with no text; the source must skip them, not crash.
        const string json = """
        {"ok":true,"result":[{"update_id":1,"message":{"message_id":2,"chat":{"id":42,"type":"private"}}}]}
        """;

        var response = JsonSerializer.Deserialize(json, TelegramJsonContext.Default.TelegramResponseUpdateArray);
        response!.Result![0].Message!.Text.Should().BeNull();
    }
}
