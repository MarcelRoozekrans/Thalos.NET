using System.Runtime.CompilerServices;
using global::Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using Thalos.Caching;

namespace Thalos.Anthropic;

/// <summary>
/// Turns the provider-neutral <see cref="PromptCacheHints"/> into Anthropic <c>cache_control</c> breakpoints, at most the
/// four Anthropic accepts, and adds the cache-write count Thalos reads to the reported usage. Sits innermost, directly
/// over the SDK's <see cref="IChatClient"/>, whose supported seams it uses: <c>WithCacheControl</c> on content and
/// <c>AdditionalProperties["CacheControl"]</c> on a tool.
/// </summary>
/// <remarks>
/// <para>
/// <b>Breakpoints.</b> A hinted tool is replaced, in a cloned tool list, by a wrapper whose additional properties carry
/// the cache control; only an <see cref="AIFunction"/> can be wrapped. Hinted instructions
/// (<see cref="PromptCacheHints.InstructionsBreakpoint"/>) move out of <see cref="ChatOptions.Instructions"/> into a
/// system message whose text carries the cache control, inserted after any leading system messages: that is where the
/// SDK would have appended the instructions, so the system prompt keeps its order. A hinted message
/// (<see cref="PromptCacheHints.Breakpoint"/>) is copied, and its last non-blank <see cref="TextContent"/>,
/// <see cref="FunctionCallContent"/> or <see cref="FunctionResultContent"/> is replaced in the copy by a marked clone.
/// Content that carries an SDK request block as its raw representation is sent as that block, which the SDK does not
/// mark, and blank text is dropped by the SDK, so neither is chosen. A message with no markable content gets no
/// breakpoint and uses none of the four.
/// </para>
/// <para>
/// <b>Cap.</b> When more than four hints are markable, the four with the highest priority win: the last hinted tool,
/// the instructions, the latest message, the message just before the first <see cref="PromptCacheHints.Transient"/>
/// message, then every other hinted message in list order, then every other hinted tool in list order. Markers the
/// caller set itself through the SDK's own seams already count toward Anthropic's limit, so they reduce the four.
/// </para>
/// <para>
/// <b>Nothing the caller passed is mutated.</b> The messages and options the SDK sees are new objects wherever a marker
/// is placed; content items are shared with the caller's messages, so a marked item is always a clone. A marked clone
/// of a <see cref="FunctionCallContent"/> or <see cref="FunctionResultContent"/> subclass is of the base type, which
/// the SDK, the only reader after this client, maps the same way. A request with no hints is passed on as it came.
/// </para>
/// <para>
/// <b>Usage.</b> The SDK already reports <see cref="UsageDetails.InputTokenCount"/> as uncached plus cache-write plus
/// cache-read input, and <see cref="UsageDetails.CachedInputTokenCount"/> as the cache reads; it reports cache writes
/// only under its own <c>CacheCreationInputTokens</c> additional count. This client copies that count to
/// <see cref="TurnUsage.CacheWriteCountKey"/>, on the response's usage and on every streamed <see cref="UsageContent"/>,
/// and changes nothing else.
/// </para>
/// </remarks>
internal sealed class AnthropicPromptCacheTranslator(IChatClient inner, CacheControlEphemeral cacheControl) : DelegatingChatClient(inner)
{
    /// <summary>Anthropic's limit on <c>cache_control</c> blocks in one request.</summary>
    internal const int MaxBreakpoints = 4;

    /// <summary>The SDK 12.50.0 additional-count key for cache-write input tokens.</summary>
    internal const string SdkCacheWriteCountKey = "CacheCreationInputTokens";

    /// <summary>The SDK 12.50.0 content additional-property key <c>WithCacheControl</c> writes; read to count markers the caller set itself.</summary>
    internal const string SdkContentCacheControlKey = "anthropic:cache_control";

    /// <summary>The tool additional-property key the SDK reads a tool's cache control from.</summary>
    private const string ToolCacheControlKey = nameof(Tool.CacheControl);

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var (translatedMessages, translatedOptions) = Translate(messages, options);
        var response = await base.GetResponseAsync(translatedMessages, translatedOptions, cancellationToken).ConfigureAwait(false);
        AddCacheWriteCount(response.Usage);
        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var (translatedMessages, translatedOptions) = Translate(messages, options);
        await foreach (var update in base.GetStreamingResponseAsync(translatedMessages, translatedOptions, cancellationToken).ConfigureAwait(false))
        {
            foreach (var content in update.Contents)
            {
                if (content is UsageContent usage)
                {
                    AddCacheWriteCount(usage.Details);
                }
            }

            yield return update;
        }
    }

    /// <summary>Copies the SDK's cache-write count to <see cref="TurnUsage.CacheWriteCountKey"/>; the rest of the usage is already normalised.</summary>
    private static void AddCacheWriteCount(UsageDetails? usage)
    {
        if (usage?.AdditionalCounts?.TryGetValue(SdkCacheWriteCountKey, out var writes) == true)
        {
            usage.AdditionalCounts[TurnUsage.CacheWriteCountKey] = writes;
        }
    }

    private (IEnumerable<ChatMessage> Messages, ChatOptions? Options) Translate(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        var plan = new Plan(list, options?.Tools, MaxBreakpoints - CountCallerMarkers(list, options?.Tools));

        // Candidates in priority order; the first ones that can be marked, up to the budget, win.
        var hintedTools = HintedToolIndices(options?.Tools);
        plan.TryTool(hintedTools.Count > 0 ? hintedTools[^1] : -1);
        plan.TryInstructions(options is { Instructions.Length: > 0 } && IsTrue(options.AdditionalProperties, PromptCacheHints.InstructionsBreakpoint));
        plan.TryMessage(list.Count - 1);
        plan.TryMessage(IndexOfFirst(list, PromptCacheHints.Transient) - 1);
        for (var i = 0; i < list.Count; i++)
        {
            plan.TryMessage(i);
        }

        foreach (var index in hintedTools)
        {
            plan.TryTool(index);
        }

        if (plan.IsEmpty)
        {
            return (messages, options);
        }

        return (MarkMessages(list, plan.Messages, plan.Instructions ? options!.Instructions : null), MarkOptions(options, plan.Tools, plan.Instructions));
    }

    private List<ChatMessage> MarkMessages(IList<ChatMessage> messages, Dictionary<int, int> marks, string? instructions)
    {
        var result = new List<ChatMessage>(messages.Count + 1);
        for (var i = 0; i < messages.Count; i++)
        {
            result.Add(marks.TryGetValue(i, out var content) ? Marked(messages[i], content) : messages[i]);
        }

        if (instructions is not null)
        {
            var leadingSystem = result.FindIndex(static m => m.Role != ChatRole.System);
            result.Insert(leadingSystem < 0 ? result.Count : leadingSystem, new ChatMessage(ChatRole.System, [new TextContent(instructions).WithCacheControl(cacheControl)]));
        }

        return result;
    }

    private ChatOptions? MarkOptions(ChatOptions? options, List<int> toolIndices, bool movedInstructions)
    {
        if (options is null || (toolIndices.Count == 0 && !movedInstructions))
        {
            return options;
        }

        var clone = options.Clone();
        if (movedInstructions)
        {
            clone.Instructions = null;
        }

        foreach (var index in toolIndices)
        {
            clone.Tools![index] = new CacheControlledFunction((AIFunction)clone.Tools[index], cacheControl);
        }

        return clone;
    }

    /// <summary>A shallow copy of <paramref name="message"/> whose content at <paramref name="contentIndex"/> is a marked clone.</summary>
    private ChatMessage Marked(ChatMessage message, int contentIndex)
    {
        var copy = message.Clone();
        var contents = new List<AIContent>(message.Contents);
        contents[contentIndex] = CloneContent(contents[contentIndex]).WithCacheControl(cacheControl);
        copy.Contents = contents;
        return copy;
    }

    private static AIContent CloneContent(AIContent content)
    {
        AIContent clone = content switch
        {
            TextContent text => new TextContent(text.Text),
            FunctionCallContent call => new FunctionCallContent(call.CallId, call.Name, call.Arguments)
            {
                Exception = call.Exception,
                InformationalOnly = call.InformationalOnly,
            },
            FunctionResultContent result => new FunctionResultContent(result.CallId, result.Result) { Exception = result.Exception },
            _ => throw new ArgumentOutOfRangeException(nameof(content), content.GetType(), "Only text, function-call and function-result content is marked."),
        };
        clone.Annotations = content.Annotations;
        clone.RawRepresentation = content.RawRepresentation;
        clone.AdditionalProperties = content.AdditionalProperties?.Clone();
        return clone;
    }

    /// <summary>The index of the last content the SDK would send as a markable block, or -1.</summary>
    private static int MarkableContentIndex(ChatMessage message)
    {
        for (var i = message.Contents.Count - 1; i >= 0; i--)
        {
            var content = message.Contents[i];
            if (content.RawRepresentation is ContentBlockParam or TextBlockParam)
            {
                continue;
            }

            if (content is TextContent text ? !string.IsNullOrWhiteSpace(text.Text) : content is FunctionCallContent or FunctionResultContent)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Cache-control markers the caller set itself through the SDK's seams, which count toward the limit.</summary>
    private static int CountCallerMarkers(IList<ChatMessage> messages, IList<AITool>? tools)
    {
        var count = 0;
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (content.AdditionalProperties?.TryGetValue(SdkContentCacheControlKey, out var value) == true && value is CacheControlEphemeral)
                {
                    count++;
                }
            }
        }

        if (tools is not null)
        {
            foreach (var tool in tools)
            {
                if (tool.AdditionalProperties.TryGetValue(ToolCacheControlKey, out var value) && value is CacheControlEphemeral)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static List<int> HintedToolIndices(IList<AITool>? tools)
    {
        var indices = new List<int>();
        for (var i = 0; tools is not null && i < tools.Count; i++)
        {
            if (tools[i].AdditionalProperties.TryGetValue(PromptCacheHints.Breakpoint, out var value) && value is true)
            {
                indices.Add(i);
            }
        }

        return indices;
    }

    private static int IndexOfFirst(IList<ChatMessage> messages, string key)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            if (IsTrue(messages[i].AdditionalProperties, key))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsTrue(AdditionalPropertiesDictionary? properties, string key) =>
        properties?.TryGetValue(key, out var value) == true && value is true;

    /// <summary>The breakpoints chosen for one request, filled in priority order until the budget runs out.</summary>
    private sealed class Plan(IList<ChatMessage> messages, IList<AITool>? tools, int budget)
    {
        private int _budget = budget;

        public List<int> Tools { get; } = [];

        /// <summary>Message index to the index of the content marked in it.</summary>
        public Dictionary<int, int> Messages { get; } = [];

        public bool Instructions { get; private set; }

        public bool IsEmpty => Tools.Count == 0 && Messages.Count == 0 && !Instructions;

        public void TryTool(int index)
        {
            if (_budget > 0 && index >= 0 && !Tools.Contains(index) && tools![index] is AIFunction)
            {
                Tools.Add(index);
                _budget--;
            }
        }

        public void TryInstructions(bool hinted)
        {
            if (_budget > 0 && hinted)
            {
                Instructions = true;
                _budget--;
            }
        }

        public void TryMessage(int index)
        {
            if (_budget > 0 && index >= 0 && !Messages.ContainsKey(index)
                && IsTrue(messages[index].AdditionalProperties, PromptCacheHints.Breakpoint)
                && MarkableContentIndex(messages[index]) is var content and >= 0)
            {
                Messages[index] = content;
                _budget--;
            }
        }
    }

    /// <summary>The inner function with the SDK's tool cache-control property added; everything else delegates.</summary>
    private sealed class CacheControlledFunction(AIFunction innerFunction, CacheControlEphemeral cacheControl) : DelegatingAIFunction(innerFunction)
    {
        public override IReadOnlyDictionary<string, object?> AdditionalProperties { get; } =
            new Dictionary<string, object?>(innerFunction.AdditionalProperties, StringComparer.Ordinal)
            {
                [ToolCacheControlKey] = cacheControl,
            };
    }
}
