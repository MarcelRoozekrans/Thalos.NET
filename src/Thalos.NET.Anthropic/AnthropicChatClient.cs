using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using ZeroAlloc.Rest.Anthropic;

namespace Thalos.Anthropic;

/// <summary>
/// The M.E.AI <see cref="IChatClient"/> over <see cref="AnthropicClient"/>, in place of the Anthropic SDK's
/// <c>AsIChatClient</c>: <see cref="AnthropicRequestMapper"/> builds the request and
/// <see cref="AnthropicResponseMapper"/> reads the answer, so requests, finish reasons and usage are what the SDK
/// 13.3.0 adapter gave for text and tools.
/// </summary>
/// <remarks>
/// A failed request throws <see cref="AnthropicApiException"/>, which carries the API's error type, status and
/// message; so does a stream that fails, sends an <c>error</c> event or ends before <c>message_stop</c>
/// (<c>stream_incomplete</c>), after the updates it did deliver. The client does not own <paramref name="client"/>;
/// disposing it does nothing.
/// </remarks>
/// <param name="client">The Anthropic client every request goes through.</param>
/// <param name="defaultModel">The model when <see cref="ChatOptions.ModelId"/> is not set.</param>
/// <param name="defaultMaxOutputTokens">The max tokens when <see cref="ChatOptions.MaxOutputTokens"/> is not set.</param>
internal sealed class AnthropicChatClient(AnthropicClient client, string defaultModel, int defaultMaxOutputTokens) : IChatClient
{
    /// <summary>The <see cref="UsageDetails.AdditionalCounts"/> key for cache-write input tokens, the SDK's name for it.</summary>
    internal const string CacheCreationInputTokensKey = "CacheCreationInputTokens";

    /// <summary>The additional-property key under which a <see cref="CacheBreakpoint"/> marks content or a tool.</summary>
    internal const string CacheBreakpointKey = "thalos.anthropic.cache_breakpoint";

    private static readonly Uri BaseAddress = new("https://api.anthropic.com/");

    private readonly AnthropicClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly string _defaultModel = defaultModel ?? throw new ArgumentNullException(nameof(defaultModel));
    private readonly int _defaultMaxOutputTokens = defaultMaxOutputTokens > 0
        ? defaultMaxOutputTokens
        : throw new ArgumentOutOfRangeException(nameof(defaultMaxOutputTokens), defaultMaxOutputTokens, "Default max tokens must be greater than zero.");

    private ChatClientMetadata? _metadata;

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var request = AnthropicRequestMapper.Map(messages, options, _defaultModel, _defaultMaxOutputTokens);
        var result = await _client.CreateMessageAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new AnthropicApiException(result.Error);
        }

        return AnthropicResponseMapper.ToChatResponse(result.Value, request.Model);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = AnthropicRequestMapper.Map(messages, options, _defaultModel, _defaultMaxOutputTokens);
        var state = new AnthropicResponseMapper.StreamState();
        await foreach (var streamEvent in _client.StreamMessageAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (state.Update(streamEvent) is { } update)
            {
                yield return update;
            }
        }

        if (state.UsageUpdate() is { } usage)
        {
            yield return usage;
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            return _metadata ??= new ChatClientMetadata("anthropic", BaseAddress, _defaultModel);
        }

        return serviceType.IsInstanceOfType(_client) ? _client
            : serviceType.IsInstanceOfType(this) ? this
            : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
