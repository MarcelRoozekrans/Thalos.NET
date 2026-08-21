namespace Thalos.Channels.Telegram;

/// <summary>
/// A Telegram Bot API call reported failure (<c>"ok": false</c>), or its response body could not be parsed as one.
/// </summary>
/// <remarks>
/// The bot token is part of every request path, never of the response body, so it can never reach this exception through
/// <see cref="ErrorCode"/> or <see cref="Description"/>. <see cref="Exception.Message"/> is built from those two members only —
/// never from the request URI — so this exception is safe to log or hand to an error aggregator as-is.
/// </remarks>
public sealed class TelegramApiException : Exception
{
    /// <summary>Creates an exception with no further detail. Prefer <see cref="TelegramApiException(int, string?, TimeSpan?)"/>.</summary>
    public TelegramApiException()
    {
    }

    /// <summary>Creates an exception with the given message. Prefer <see cref="TelegramApiException(int, string?, TimeSpan?)"/>.</summary>
    /// <param name="message">The exception message.</param>
    public TelegramApiException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with the given message and inner exception. Prefer <see cref="TelegramApiException(int, string?, TimeSpan?)"/>.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public TelegramApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates an exception describing a failed Telegram Bot API call.</summary>
    /// <param name="errorCode">Telegram's HTTP-like error code (e.g. 400, 401, 403, 429), or the HTTP status code when Telegram did not report one.</param>
    /// <param name="description">Telegram's human-readable failure description, or <see langword="null"/> when none was provided.</param>
    /// <param name="retryAfter">
    /// How long the caller should wait before retrying, taken from a 429 response's <c>parameters.retry_after</c>;
    /// <see langword="null"/> for every other error.
    /// </param>
    public TelegramApiException(int errorCode, string? description, TimeSpan? retryAfter)
        : base(BuildMessage(errorCode, description))
    {
        ErrorCode = errorCode;
        Description = description;
        RetryAfter = retryAfter;
    }

    /// <summary>Telegram's HTTP-like error code, or the HTTP status code when Telegram did not report one.</summary>
    public int ErrorCode { get; }

    /// <summary>Telegram's human-readable failure description, or <see langword="null"/> when none was provided.</summary>
    public string? Description { get; }

    /// <summary>How long the caller should wait before retrying; present only for 429 responses.</summary>
    public TimeSpan? RetryAfter { get; }

    private static string BuildMessage(int errorCode, string? description) =>
        string.IsNullOrEmpty(description)
            ? $"Telegram Bot API call failed with error {errorCode}."
            : $"Telegram Bot API call failed with error {errorCode}: {description}";
}
