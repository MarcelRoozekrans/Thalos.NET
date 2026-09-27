using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Thalos.Anthropic;

/// <summary>Registers Anthropic as the Thalos chat-client provider.</summary>
/// <remarks>
/// <see cref="AnthropicOptions"/> are validated when first resolved and at host start (<c>ValidateOnStart</c>) via
/// <see cref="AnthropicOptions.Describe"/>; a violation, such as a <see cref="AnthropicPromptCachingOptions.Ttl"/> other
/// than <c>5m</c> or <c>1h</c>, throws <see cref="OptionsValidationException"/>.
/// </remarks>
public static class AnthropicThalosBuilderExtensions
{
    /// <summary>Uses Anthropic as the chat-client provider, configured in code (API key falls back to ANTHROPIC_API_KEY).</summary>
    public static ThalosBuilder UseAnthropic(this ThalosBuilder builder, Action<AnthropicOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return Register(builder, builder.Services.AddOptions<AnthropicOptions>().Configure(o => configure?.Invoke(o)));
    }

    /// <summary>Uses Anthropic as the chat-client provider, bound from the <c>Thalos:Anthropic</c> section of <paramref name="configuration"/>.</summary>
    public static ThalosBuilder UseAnthropic(this ThalosBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);
        return Register(builder, builder.Services.AddOptions<AnthropicOptions>().Bind(configuration.GetSection(AnthropicOptions.SectionName)));
    }

    private static ThalosBuilder Register(ThalosBuilder builder, OptionsBuilder<AnthropicOptions> options)
    {
        options.ValidateOnStart();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AnthropicOptions>, AnthropicOptionsValidator>());
        return builder.UseChatClientProvider<AnthropicChatClientProvider>();
    }

    /// <summary>Runs <see cref="AnthropicOptions.Describe"/> when the options are first resolved, and at host start via <c>ValidateOnStart</c>.</summary>
    private sealed class AnthropicOptionsValidator : IValidateOptions<AnthropicOptions>
    {
        public ValidateOptionsResult Validate(string? name, AnthropicOptions options) =>
            AnthropicOptions.Describe(options) is { } violation ? ValidateOptionsResult.Fail(AnthropicOptions.SectionName + ": " + violation) : ValidateOptionsResult.Success;
    }
}
