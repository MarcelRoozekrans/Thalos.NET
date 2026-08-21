using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Thalos.Channels.Telegram;

/// <summary>
/// A thin client for the four Telegram Bot API methods this package needs: <c>getUpdates</c>, <c>sendMessage</c>,
/// <c>editMessageText</c> and <c>sendChatAction</c>.
/// </summary>
/// <remarks>
/// Hand-rolled over <see cref="HttpClient"/> rather than a generated REST client: this package targets <c>net8.0</c> as well as
/// <c>net10.0</c>, and the alternative in this codebase (<c>ZeroAlloc.Rest</c>) is <c>net10.0</c>-only. Every call is serialized
/// and deserialized through the source-generated <see cref="TelegramJsonContext"/>, never through a reflection-based
/// <see cref="JsonSerializer"/> overload, so the package stays AOT-friendly. This client is a pure transport: it issues a
/// request and maps the response. Poll cadence, backoff and honouring <see cref="TelegramApiException.RetryAfter"/> are the
/// caller's concern; socket-level timeouts are <see cref="HttpClient"/>'s.
/// </remarks>
public sealed class TelegramBotClient
{
    private const string NotModifiedFragment = "message is not modified";

    private readonly HttpClient _httpClient;
    private readonly string _token;

    /// <summary>Creates a client that issues Bot API calls for <paramref name="token"/> through <paramref name="httpClient"/>.</summary>
    /// <param name="httpClient">The client requests are sent with. Its <see cref="HttpClient.BaseAddress"/> should be the Bot API host.</param>
    /// <param name="token">The bot token. Embedded in the request path per Telegram's convention; never logged or echoed into an exception message.</param>
    public TelegramBotClient(HttpClient httpClient, string token)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        _httpClient = httpClient;
        _token = token;
    }

    /// <summary>Long-polls <c>getUpdates</c> for updates after <paramref name="offset"/>, asking Telegram to wait up to <paramref name="timeoutSeconds"/> for one to arrive.</summary>
    /// <param name="offset">The lowest update id not yet acknowledged; Telegram returns only updates at or after it.</param>
    /// <param name="timeoutSeconds">How many seconds Telegram should hold the request open waiting for new updates.</param>
    /// <param name="ct">A token to cancel the wait.</param>
    /// <returns>The updates Telegram returned; empty when none arrived before the timeout.</returns>
    /// <exception cref="TelegramApiException">Telegram reported a failure.</exception>
    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct)
    {
        var request = new GetUpdatesRequest(offset, timeoutSeconds);
        return await CallAsync(
            "getUpdates",
            request,
            TelegramJsonContext.Default.GetUpdatesRequest,
            TelegramJsonContext.Default.TelegramResponseUpdateArray,
            ct).ConfigureAwait(false);
    }

    /// <summary>Sends a new message via <c>sendMessage</c>.</summary>
    /// <param name="chatId">The destination chat.</param>
    /// <param name="text">The message text.</param>
    /// <param name="parseMode">The formatting mode applied to <paramref name="text"/> (e.g. <c>"MarkdownV2"</c>), or <see langword="null"/> for plain text.</param>
    /// <param name="ct">A token to cancel the call.</param>
    /// <returns>The message Telegram created.</returns>
    /// <exception cref="TelegramApiException">Telegram reported a failure, including a malformed <paramref name="text"/> for <paramref name="parseMode"/>.</exception>
    public async Task<TelegramMessage> SendMessageAsync(long chatId, string text, string? parseMode, CancellationToken ct)
    {
        var request = new SendMessageRequest(chatId, text, parseMode);
        return await CallAsync(
            "sendMessage",
            request,
            TelegramJsonContext.Default.SendMessageRequest,
            TelegramJsonContext.Default.TelegramResponseMessage,
            ct).ConfigureAwait(false);
    }

    /// <summary>Replaces the text of an existing message via <c>editMessageText</c>.</summary>
    /// <param name="chatId">The chat the message belongs to.</param>
    /// <param name="messageId">The message to edit.</param>
    /// <param name="text">The replacement text.</param>
    /// <param name="parseMode">The formatting mode applied to <paramref name="text"/> (e.g. <c>"MarkdownV2"</c>), or <see langword="null"/> for plain text.</param>
    /// <param name="ct">A token to cancel the call.</param>
    /// <returns>
    /// The edited message, or <see langword="null"/> when Telegram rejected the edit because <paramref name="text"/> is identical to
    /// the message's current text — that response means "nothing to do", not a failure, so it is not thrown for this endpoint.
    /// </returns>
    /// <exception cref="TelegramApiException">Telegram reported any other failure.</exception>
    public async Task<TelegramMessage?> EditMessageTextAsync(long chatId, long messageId, string text, string? parseMode, CancellationToken ct)
    {
        var request = new EditMessageTextRequest(chatId, messageId, text, parseMode);
        return await CallAllowingNotModifiedAsync(
            "editMessageText",
            request,
            TelegramJsonContext.Default.EditMessageTextRequest,
            TelegramJsonContext.Default.TelegramResponseMessage,
            ct).ConfigureAwait(false);
    }

    /// <summary>Shows a transient chat action (e.g. <c>"typing"</c>) via <c>sendChatAction</c>.</summary>
    /// <param name="chatId">The chat to show the action in.</param>
    /// <param name="action">The action to display.</param>
    /// <param name="ct">A token to cancel the call.</param>
    /// <exception cref="TelegramApiException">Telegram reported a failure.</exception>
    public async Task SendChatActionAsync(long chatId, string action, CancellationToken ct)
    {
        var request = new SendChatActionRequest(chatId, action);
        _ = await CallAsync(
            "sendChatAction",
            request,
            TelegramJsonContext.Default.SendChatActionRequest,
            TelegramJsonContext.Default.TelegramResponseBool,
            ct).ConfigureAwait(false);
    }

    /// <summary>Posts <paramref name="request"/> to <paramref name="method"/> and returns its result. Throws on any non-ok response.</summary>
    private async Task<TResult> CallAsync<TRequest, TResult>(
        string method,
        TRequest request,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TelegramResponse<TResult>> responseTypeInfo,
        CancellationToken ct)
    {
        var (response, statusCode) = await PostAsync(method, request, requestTypeInfo, responseTypeInfo, ct).ConfigureAwait(false);
        if (response.Ok)
        {
            return response.Result ?? throw new TelegramApiException(0, $"Telegram reported success for {method} without a result.", retryAfter: null);
        }

        throw ToException(response, statusCode);
    }

    /// <summary>
    /// Posts <paramref name="request"/> to <paramref name="method"/> and returns its result. On a 400 response whose description
    /// says the edit did not change anything, returns <see langword="null"/> instead of throwing; every other non-ok response throws.
    /// </summary>
    private async Task<TResult?> CallAllowingNotModifiedAsync<TRequest, TResult>(
        string method,
        TRequest request,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TelegramResponse<TResult>> responseTypeInfo,
        CancellationToken ct)
        where TResult : class
    {
        var (response, statusCode) = await PostAsync(method, request, requestTypeInfo, responseTypeInfo, ct).ConfigureAwait(false);
        if (response.Ok)
        {
            return response.Result;
        }

        if (response.ErrorCode == 400 && response.Description?.Contains(NotModifiedFragment, StringComparison.Ordinal) == true)
        {
            return null;
        }

        throw ToException(response, statusCode);
    }

    private async Task<(TelegramResponse<TResult> Response, HttpStatusCode StatusCode)> PostAsync<TRequest, TResult>(
        string method,
        TRequest request,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TelegramResponse<TResult>> responseTypeInfo,
        CancellationToken ct)
    {
        // The token belongs in the path, per Telegram's convention. It must never be logged and never appear in an exception
        // message: everything below reads only the parsed response body, never the request URI, when building failures.
        using var content = JsonContent.Create(request, requestTypeInfo);
        using var httpResponse = await _httpClient.PostAsync($"bot{_token}/{method}", content, ct).ConfigureAwait(false);
        var body = await httpResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var bodyDisposal = body.ConfigureAwait(false);
        var response = await JsonSerializer.DeserializeAsync(body, responseTypeInfo, ct).ConfigureAwait(false)
            ?? throw new TelegramApiException(0, $"Telegram returned an empty response body for {method}.", retryAfter: null);

        return (response, httpResponse.StatusCode);
    }

    /// <summary>
    /// The largest <see cref="TelegramApiException.RetryAfter"/> this client will ever report, regardless of what
    /// a response's <c>parameters.retry_after</c> says. That field is attacker-influenced — it comes verbatim from
    /// whatever answered the HTTP request — and an unclamped value (negative, or absurdly large) fed straight into
    /// <see cref="TimeSpan.FromSeconds(double)"/> and then a caller's <c>Task.Delay</c> can throw
    /// <see cref="ArgumentOutOfRangeException"/> well outside anything a network response should be able to trigger.
    /// </summary>
    private const int MaxRetryAfterSeconds = 300;

    private static TelegramApiException ToException<TResult>(TelegramResponse<TResult> response, HttpStatusCode statusCode)
    {
        var errorCode = response.ErrorCode ?? (int)statusCode;
        var retryAfter = response.Parameters?.RetryAfter is { } seconds
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, 0, MaxRetryAfterSeconds))
            : (TimeSpan?)null;
        return new TelegramApiException(errorCode, response.Description, retryAfter);
    }
}
