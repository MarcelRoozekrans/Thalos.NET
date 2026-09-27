using global::Anthropic;
using global::Anthropic.Core;
using global::Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Thalos.Anthropic;

/// <summary><see cref="IChatClientProvider"/> backed by the official Anthropic SDK (<see cref="AnthropicClient"/>).</summary>
/// <remarks>
/// The provider owns one lazily-created <see cref="AnthropicClient"/> (the HTTP transport), shared by every agent, and disposes it
/// with itself. Each <see cref="CreateChatClient"/> call returns a thin <see cref="IChatClient"/> over that shared client, wrapped in
/// the prompt-cache translator, which translates cache hints only when <see cref="AnthropicPromptCachingOptions.Enabled"/> is set
/// and always reports cache writes; per the
/// <see cref="IChatClientProvider"/> contract the returned client is owned and disposed by the caller, and disposing it does
/// <em>not</em> tear the shared transport down.
/// </remarks>
public sealed class AnthropicChatClientProvider : IChatClientProvider, IDisposable
{
    private readonly AnthropicOptions _options;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Lazy<AnthropicClient> _client;
    private readonly HttpClient? _httpClient;
    private readonly CacheControlEphemeral _cacheControl;

    /// <summary>Creates a provider that resolves the API key from <paramref name="options"/> or the ANTHROPIC_API_KEY environment variable.</summary>
    public AnthropicChatClientProvider(IOptions<AnthropicOptions> options)
        : this(options, Environment.GetEnvironmentVariable)
    {
    }

    internal AnthropicChatClientProvider(IOptions<AnthropicOptions> options, Func<string, string?> getEnvironmentVariable)
        : this(options, getEnvironmentVariable, httpClient: null)
    {
    }

    /// <summary>A provider whose SDK client sends every request through <paramref name="httpClient"/>; used by tests to record requests.</summary>
    internal AnthropicChatClientProvider(IOptions<AnthropicOptions> options, HttpClient httpClient)
        : this(options, Environment.GetEnvironmentVariable, httpClient ?? throw new ArgumentNullException(nameof(httpClient)))
    {
    }

    /// <summary>Validates the options and prepares the lazily-created SDK client; <paramref name="httpClient"/> null keeps the SDK's own.</summary>
    /// <exception cref="ArgumentException">The options are invalid, as <see cref="AnthropicOptions.Describe"/> reports.</exception>
    private AnthropicChatClientProvider(IOptions<AnthropicOptions> options, Func<string, string?> getEnvironmentVariable, HttpClient? httpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        _options = options.Value;
        if (AnthropicOptions.Describe(_options) is { } violation)
        {
            throw new ArgumentException(AnthropicOptions.SectionName + ": " + violation, nameof(options));
        }

        _getEnvironmentVariable = getEnvironmentVariable;
        _httpClient = httpClient;
        _cacheControl = new CacheControlEphemeral
        {
            Ttl = string.Equals(_options.PromptCaching.Ttl, AnthropicPromptCachingOptions.OneHour, StringComparison.Ordinal) ? Ttl.Ttl1h : Ttl.Ttl5m,
        };
        _client = new Lazy<AnthropicClient>(CreateClient, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public string Name => "anthropic";

    /// <inheritdoc />
    public string DefaultModel => _options.DefaultModel;

    /// <summary>Whether the shared <see cref="AnthropicClient"/> has been created (i.e. <see cref="CreateChatClient"/> has succeeded at least once).</summary>
    internal bool IsClientCreated => _client.IsValueCreated;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">No API key is configured and ANTHROPIC_API_KEY is not set.</exception>
    public IChatClient CreateChatClient(AgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var client = _client.Value.AsIChatClient(agent.Model ?? _options.DefaultModel, agent.MaxOutputTokens ?? _options.DefaultMaxOutputTokens);
        return new AnthropicPromptCacheTranslator(client, _options.PromptCaching.Enabled ? _cacheControl : null);
    }

    /// <summary>Disposes the shared <see cref="AnthropicClient"/> if it was created.</summary>
    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }

    private AnthropicClient CreateClient()
    {
        var apiKey = string.IsNullOrWhiteSpace(_options.ApiKey) ? _getEnvironmentVariable("ANTHROPIC_API_KEY") : _options.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Anthropic API key missing. Set Thalos:Anthropic:ApiKey or the ANTHROPIC_API_KEY environment variable.");
        }

        var clientOptions = new ClientOptions { ApiKey = apiKey };
        if (_httpClient is not null)
        {
            clientOptions.HttpClient = _httpClient;
        }

        if (_options.Timeout is { } timeout)
        {
            clientOptions.Timeout = timeout;
        }

        if (_options.MaxRetries is { } retries)
        {
            clientOptions.MaxRetries = retries;
        }

        return new AnthropicClient(clientOptions);
    }
}
