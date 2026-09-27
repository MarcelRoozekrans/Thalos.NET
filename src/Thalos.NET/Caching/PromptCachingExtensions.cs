using Microsoft.Extensions.AI;

namespace Thalos.Caching;

/// <summary>Adds <see cref="PromptCachingChatClient"/> to a Microsoft.Extensions.AI pipeline or to every Thalos agent.</summary>
public static class PromptCachingExtensions
{
    /// <summary>
    /// Adds a <see cref="PromptCachingChatClient"/> at this point of the pipeline. Place it inside any function-invocation
    /// client, so it runs on every model round trip, and outside the provider's client, whose translator reads the hints.
    /// </summary>
    public static ChatClientBuilder UsePromptCaching(this ChatClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Use(static inner => new PromptCachingChatClient(inner));
    }

    /// <summary>
    /// Places prompt-cache hints on every agent's model round trips, as the outermost chat-client decorator
    /// (<see cref="IChatClientDecorator.Order"/> <see cref="int.MaxValue"/>). Calling this twice is a no-op.
    /// </summary>
    public static ThalosBuilder UsePromptCaching(this ThalosBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddChatClientDecorator<PromptCachingDecorator>();
    }
}
