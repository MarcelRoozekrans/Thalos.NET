using System.Globalization;
using Thalos.Channels.Telegram;
using Thalos.Tests.Channels.Telegram.Fakes;

namespace Thalos.Tests.Channels.Telegram;

/// <summary>
/// Covers <c>LogRejectedSender</c> specifically: the rest of <see cref="TelegramChannelSourceTests"/> proves the
/// rejected-sender gate drops the update, but only <see cref="CapturingLogger{T}.Entries"/>'s structured state can
/// prove the logged <c>SenderId</c> field is the numeric id itself, rather than a pre-formatted string.
/// </summary>
public sealed class TelegramChannelSourceLoggingTests
{
    private static string Update(long updateId, long userId) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{{\"ok\":true,\"result\":[{{\"update_id\":{updateId},\"message\":{{\"message_id\":1,\"text\":\"hello\",\"chat\":{{\"id\":42,\"type\":\"private\"}},\"from\":{{\"id\":{userId},\"is_bot\":false}}}}}}]}}");

    [Fact]
    public async Task Rejected_sender_is_logged_with_a_numeric_SenderId_state_field()
    {
        var client = new TelegramBotClient(
            new HttpClient(new StubHandler(StubHandler.Json(Update(1, userId: 222))))
            {
                BaseAddress = new Uri("https://api.telegram.org/"),
            }, "T");

        var logger = new CapturingLogger<TelegramChannelSource>();
        var source = new TelegramChannelSource(client, new TelegramOptions
        {
            BotToken = "T",
            Enabled = true,
            AllowedUserIds = [111],
            PrincipalId = "telegram:test",
            Roles = [],
        }, logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await foreach (var _ in source.ReadAsync(cts.Token))
            {
                // The sender is not allow-listed, so nothing is ever yielded; this only runs out the CTS timeout,
                // same pattern as the other drop-and-never-answer cases in TelegramChannelSourceTests.
            }
        }
        catch (OperationCanceledException)
        {
        }

        var entry = logger.Entries.Single(e => e.EventId.Id == 705);
        entry.State.Should().ContainSingle(kv => kv.Key == "SenderId")
            .Which.Value.Should().BeOfType<long>().And.Be(222L);
    }
}
