namespace Thalos.Caching;

/// <summary>
/// Provider-neutral prompt-cache hints, set as <c>AdditionalProperties</c> keys. <see cref="PromptCachingChatClient"/>
/// places them; a provider-specific translator inside the provider's client turns them into that provider's cache
/// controls. A provider with no translator ignores them.
/// </summary>
public static class PromptCacheHints
{
    /// <summary>
    /// On <c>AITool.AdditionalProperties</c>, <c>ChatMessage.AdditionalProperties</c> or
    /// <c>ChatOptions.AdditionalProperties</c>: <see langword="true"/> marks a cache breakpoint at the end of that item.
    /// </summary>
    /// <remarks>
    /// A caller may set this itself, but <see cref="PromptCachingChatClient"/> passes caller-placed hints through
    /// unchanged and adds its own, so every caller-placed hint counts toward the provider's breakpoint limit (four on
    /// Anthropic). Hints set on messages the caller keeps in its history are sent again on every later round trip.
    /// </remarks>
    public const string Breakpoint = "thalos.cache.breakpoint";

    /// <summary>On <c>ChatOptions.AdditionalProperties</c>: <see langword="true"/> marks the end of the system instructions.</summary>
    public const string InstructionsBreakpoint = "thalos.cache.breakpoint.instructions";

    /// <summary>On <c>ChatMessage.AdditionalProperties</c>: a per-turn message that history stores must not persist.</summary>
    public const string Transient = "thalos.transient";
}
