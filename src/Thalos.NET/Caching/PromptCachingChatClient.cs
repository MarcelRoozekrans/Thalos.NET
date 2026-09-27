using Microsoft.Extensions.AI;

namespace Thalos.Caching;

/// <summary>
/// Places provider-neutral <see cref="PromptCacheHints"/> on three boundaries of each request: the last tool, the end
/// of the instructions and the latest message. Placement follows positions alone: messages are never reordered,
/// added or removed.
/// </summary>
/// <remarks>
/// <para>
/// The caller's messages and options are never mutated. The inner client gets a clone of the
/// <see cref="ChatOptions"/>, whose <see cref="ChatOptions.AdditionalProperties"/> and <see cref="ChatOptions.Tools"/>
/// are the clone's own, and a new message list whose last entry is a shallow copy of the caller's last message: every
/// member is kept, its <see cref="ChatMessage.Contents"/> list and <see cref="ChatMessage.AdditionalProperties"/> are
/// new collections holding the same items, and the hint is added to the copy only. Because nothing is written back,
/// history never accumulates breakpoints across round trips.
/// </para>
/// <para>
/// <b>Tool:</b> the last tool, when it is an <see cref="AIFunction"/>, is replaced in the cloned list by a wrapper that
/// adds the hint to its <see cref="AITool.AdditionalProperties"/> and otherwise behaves exactly like it, invocation
/// included. Any other kind of last tool is left unhinted. <b>Instructions:</b> set when
/// <see cref="ChatOptions.Instructions"/> is non-empty. <b>Message:</b> the last message, whatever its role.
/// </para>
/// </remarks>
public sealed class PromptCachingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    /// <inheritdoc />
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return base.GetResponseAsync(HintMessages(messages), HintOptions(options), cancellationToken);
    }

    /// <inheritdoc />
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return base.GetStreamingResponseAsync(HintMessages(messages), HintOptions(options), cancellationToken);
    }

    private static List<ChatMessage> HintMessages(IEnumerable<ChatMessage> messages)
    {
        var list = messages.ToList();
        if (list.Count == 0)
        {
            return list;
        }

        var last = list[^1];
        var copy = last.Clone();
        copy.Contents = [.. last.Contents];
        copy.AdditionalProperties = last.AdditionalProperties?.Clone() ?? [];
        copy.AdditionalProperties[PromptCacheHints.Breakpoint] = true;
        list[^1] = copy;
        return list;
    }

    private static ChatOptions? HintOptions(ChatOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        var clone = options.Clone();
        if (!string.IsNullOrEmpty(clone.Instructions))
        {
            clone.AdditionalProperties ??= [];
            clone.AdditionalProperties[PromptCacheHints.InstructionsBreakpoint] = true;
        }

        if (clone.Tools is { Count: > 0 } tools && tools[^1] is AIFunction function)
        {
            tools[^1] = new HintedFunction(function);
        }

        return clone;
    }

    /// <summary>The inner function with <see cref="PromptCacheHints.Breakpoint"/> added to its additional properties; everything else delegates.</summary>
    private sealed class HintedFunction(AIFunction innerFunction) : DelegatingAIFunction(innerFunction)
    {
        public override IReadOnlyDictionary<string, object?> AdditionalProperties { get; } =
            new Dictionary<string, object?>(innerFunction.AdditionalProperties, StringComparer.Ordinal)
            {
                [PromptCacheHints.Breakpoint] = true,
            };
    }
}
