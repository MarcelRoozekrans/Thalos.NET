namespace Thalos.Anthropic;

/// <summary>
/// Prompt caching for <see cref="AnthropicChatClientProvider"/>, bound from <c>Thalos:Anthropic:PromptCaching</c>. When
/// enabled, each chat client translates the provider-neutral <c>Thalos.Caching.PromptCacheHints</c> into Anthropic
/// <c>cache_control</c> breakpoints and reports cache writes under <see cref="TurnUsage.CacheWriteCountKey"/>.
/// </summary>
/// <remarks>
/// Enabling this places no breakpoint by itself: a request carries markers only where something hinted one, normally
/// <c>UsePromptCaching()</c>. A request with no hints is sent exactly as it would be with caching disabled.
/// </remarks>
public sealed class AnthropicPromptCachingOptions
{
    /// <summary>The time-to-live Anthropic's default cache breakpoint uses: five minutes.</summary>
    public const string FiveMinutes = "5m";

    /// <summary>The extended time-to-live: one hour. Cache writes cost more than with <see cref="FiveMinutes"/>.</summary>
    public const string OneHour = "1h";

    /// <summary>Whether cache hints are translated into <c>cache_control</c> breakpoints. On by default.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The time-to-live of every breakpoint: <see cref="FiveMinutes"/> (the default) or <see cref="OneHour"/>, matched
    /// exactly. Any other value fails options validation, whether or not caching is enabled.
    /// </summary>
    public string Ttl { get; set; } = FiveMinutes;

    /// <summary>The first violation as text, or null when the options are valid.</summary>
    public static string? Describe(AnthropicPromptCachingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Ttl is FiveMinutes or OneHour
            ? null
            : $"PromptCaching:Ttl must be \"{FiveMinutes}\" or \"{OneHour}\" (was \"{options.Ttl}\").";
    }
}
