using Microsoft.Extensions.AI;

namespace Thalos.Caching;

/// <summary>
/// Places provider-neutral <see cref="PromptCacheHints"/> on up to four boundaries of each request: the last tool, the
/// end of the instructions, the latest message and, when the request holds a <see cref="PromptCacheHints.Transient"/>
/// message, the last message before the first one. Placement follows positions alone: messages are never reordered,
/// added or removed.
/// </summary>
/// <remarks>
/// <para>
/// The caller's messages and options are never mutated. The inner client gets a clone of the
/// <see cref="ChatOptions"/>, whose <see cref="ChatOptions.AdditionalProperties"/> and <see cref="ChatOptions.Tools"/>
/// are the clone's own, and a new message list in which each hinted entry is a shallow copy of the caller's message:
/// every member is kept, its <see cref="ChatMessage.Contents"/> list and <see cref="ChatMessage.AdditionalProperties"/>
/// are new collections holding the same items, and the hint is added to the copy only. Because nothing is written back,
/// history never accumulates the breakpoints this client places.
/// </para>
/// <para>
/// <b>Caller-placed hints pass through unchanged.</b> A <see cref="PromptCacheHints"/> key the caller already set on a
/// message, a tool or the options is neither removed nor deduplicated, and this client adds its own, up to four, on top. Each
/// caller-placed hint therefore counts toward the provider's breakpoint limit, and one on a message the caller keeps in
/// its history is sent again on every round trip, so a caller-hinted history can exceed that limit. Capping markers is
/// the provider translator's job.
/// </para>
/// <para>
/// <b>Tool:</b> the last tool, when it is an <see cref="AIFunction"/>, is replaced in the cloned list by a wrapper that
/// adds the hint to its <see cref="AITool.AdditionalProperties"/> and otherwise behaves exactly like it, invocation
/// included. Any other kind of last tool is left unhinted. <b>Instructions:</b> set when
/// <see cref="ChatOptions.Instructions"/> is non-empty. <b>Message:</b> the last message, whatever its role.
/// <b>Before the transient message:</b> the message directly before the first <see cref="PromptCacheHints.Transient"/>
/// message, such as the recalled-memories block. That message ends the stored history, and a transient message is never
/// stored, so without this breakpoint the next turn's request would share no cached boundary past the instructions
/// with this one and the whole history would be written to the cache again on every turn. Nothing is added when the
/// first message is transient, when no message is, or when that message already carries a breakpoint hint.
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

        list[^1] = Hinted(list[^1]);
        var transient = list.FindIndex(static m => HasTrue(m, PromptCacheHints.Transient));
        if (transient > 0 && !HasTrue(list[transient - 1], PromptCacheHints.Breakpoint))
        {
            list[transient - 1] = Hinted(list[transient - 1]);
        }

        return list;
    }

    /// <summary>A shallow copy of <paramref name="message"/> with its own contents list and properties, plus the breakpoint.</summary>
    private static ChatMessage Hinted(ChatMessage message)
    {
        var copy = message.Clone();
        copy.Contents = [.. message.Contents];
        copy.AdditionalProperties = message.AdditionalProperties?.Clone() ?? [];
        copy.AdditionalProperties[PromptCacheHints.Breakpoint] = true;
        return copy;
    }

    private static bool HasTrue(ChatMessage message, string key) =>
        message.AdditionalProperties?.TryGetValue(key, out var value) == true && value is true;

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
