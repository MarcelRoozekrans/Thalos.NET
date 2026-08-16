using global::Anthropic;
using global::Anthropic.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Thalos.Anthropic;

/// <summary><see cref="IChatClientProvider"/> backed by the official Anthropic SDK (<see cref="AnthropicClient"/>).</summary>
public sealed class AnthropicChatClientProvider(IOptions<AnthropicOptions> options) : IChatClientProvider
{
    private readonly AnthropicOptions _options = options.Value;

    /// <inheritdoc />
    public string Name => "anthropic";

    /// <inheritdoc />
    public string DefaultModel => _options.DefaultModel;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">No API key is configured and ANTHROPIC_API_KEY is not set.</exception>
    public IChatClient CreateChatClient(AgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var apiKey = string.IsNullOrWhiteSpace(_options.ApiKey) ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") : _options.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Anthropic API key missing. Set Thalos:Anthropic:ApiKey or the ANTHROPIC_API_KEY environment variable.");
        }

        var clientOptions = new ClientOptions { ApiKey = apiKey };
        if (_options.Timeout is { } timeout)
        {
            clientOptions.Timeout = timeout;
        }

        if (_options.MaxRetries is { } retries)
        {
            clientOptions.MaxRetries = retries;
        }

        var client = new AnthropicClient(clientOptions);
        return client.AsIChatClient(agent.Model ?? _options.DefaultModel, agent.MaxOutputTokens ?? _options.DefaultMaxOutputTokens);
    }
}
