using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Thalos.Anthropic;

/// <summary>Registers Anthropic as the Thalos chat-client provider.</summary>
public static class AnthropicThalosBuilderExtensions
{
    /// <summary>Uses Anthropic as the chat-client provider, configured in code (API key falls back to ANTHROPIC_API_KEY).</summary>
    public static ThalosBuilder UseAnthropic(this ThalosBuilder builder, Action<AnthropicOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOptions<AnthropicOptions>().Configure(o => configure?.Invoke(o));
        return builder.UseChatClientProvider<AnthropicChatClientProvider>();
    }

    /// <summary>Uses Anthropic as the chat-client provider, bound from the <c>Thalos:Anthropic</c> section of <paramref name="configuration"/>.</summary>
    public static ThalosBuilder UseAnthropic(this ThalosBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);
        builder.Services.AddOptions<AnthropicOptions>().Bind(configuration.GetSection(AnthropicOptions.SectionName));
        return builder.UseChatClientProvider<AnthropicChatClientProvider>();
    }
}
