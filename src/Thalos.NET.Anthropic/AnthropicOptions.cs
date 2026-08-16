namespace Thalos.Anthropic;

/// <summary>Configuration for <see cref="AnthropicChatClientProvider"/>. Bound from <c>Thalos:Anthropic</c> when using <see cref="AnthropicThalosBuilderExtensions.UseAnthropic(ThalosBuilder, Microsoft.Extensions.Configuration.IConfiguration)"/>.</summary>
public sealed class AnthropicOptions
{
    /// <summary>Configuration section name: <c>Thalos:Anthropic</c>.</summary>
    public const string SectionName = "Thalos:Anthropic";

    /// <summary>Falls back to the ANTHROPIC_API_KEY environment variable when empty.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Model used when <see cref="AgentDefinition.Model"/> is null. The built-in default is a snapshot of the model line current
    /// when this package was written; hosts should set the model id they actually want (Anthropic retires and renames models).
    /// </summary>
    public string DefaultModel { get; set; } = "claude-sonnet-4-5";

    /// <summary>Max output tokens used when <see cref="AgentDefinition.MaxOutputTokens"/> is null.</summary>
    public int DefaultMaxOutputTokens { get; set; } = 8192;

    /// <summary>Per-request timeout; null → SDK default.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>SDK retry count; null → SDK default.</summary>
    public int? MaxRetries { get; set; }
}
