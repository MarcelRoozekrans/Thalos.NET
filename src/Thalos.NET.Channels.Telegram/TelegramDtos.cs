namespace Thalos.Channels.Telegram;

/// <summary>The envelope every Telegram Bot API call responds with.</summary>
/// <typeparam name="T">The shape of <see cref="Result"/> for this call.</typeparam>
/// <param name="Ok">Whether the call succeeded.</param>
/// <param name="Result">The payload, present only when <paramref name="Ok"/> is <see langword="true"/>.</param>
/// <param name="ErrorCode">The HTTP-like error code Telegram reports, present only on failure.</param>
/// <param name="Description">A human-readable description of the failure.</param>
/// <param name="Parameters">Extra data about the failure, such as a flood-control retry delay.</param>
public sealed record TelegramResponse<T>(
    bool Ok,
    T? Result,
    int? ErrorCode,
    string? Description,
    TelegramResponseParameters? Parameters);

/// <summary>Extra data Telegram attaches to a failed response.</summary>
/// <param name="RetryAfter">The number of seconds to wait before retrying, present on 429 responses.</param>
public sealed record TelegramResponseParameters(int? RetryAfter);

/// <summary>A single update returned by Telegram's getUpdates long-poll.</summary>
/// <param name="UpdateId">The update's sequence number; the source acknowledges up to and including it.</param>
/// <param name="Message">The message carried by this update, when the update is a message.</param>
public sealed record TelegramUpdate(long UpdateId, TelegramMessage? Message);

/// <summary>A Telegram message.</summary>
/// <param name="MessageId">The message's identifier within its chat.</param>
/// <param name="Text">The message text, or <see langword="null"/> for photos, stickers, joins and similar.</param>
/// <param name="Chat">The chat the message was sent in.</param>
/// <param name="From">The user who sent the message, absent for channel posts.</param>
public sealed record TelegramMessage(long MessageId, string? Text, TelegramChat Chat, TelegramUser? From);

/// <summary>The chat a Telegram message belongs to.</summary>
/// <param name="Id">The chat's identifier.</param>
/// <param name="Type">The chat's kind: "private", "group", "supergroup" or "channel".</param>
public sealed record TelegramChat(long Id, string Type);

/// <summary>A Telegram user.</summary>
/// <param name="Id">The user's identifier.</param>
/// <param name="IsBot">Whether the user is a bot.</param>
public sealed record TelegramUser(long Id, bool IsBot);
