using System.Runtime.CompilerServices;
using global::Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using Thalos.Caching;

namespace Thalos.Anthropic;

/// <summary>
/// Turns the provider-neutral <see cref="PromptCacheHints"/> into Anthropic <c>cache_control</c> breakpoints, at most the
/// four Anthropic accepts, and adds the cache-write count Thalos reads to the reported usage. Sits innermost, directly
/// over the SDK's <see cref="IChatClient"/>, whose supported seams it uses: <c>WithCacheControl</c> on content and
/// <c>AdditionalProperties["CacheControl"]</c> on a tool. Constructed with no cache control, it translates no hints and
/// only adds the cache-write count, so a provider with caching disabled still reports cache writes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Breakpoints.</b> A hinted tool is replaced, in a cloned tool list, by a wrapper whose additional properties carry
/// the cache control; only an <see cref="AIFunction"/> can be wrapped. Hinted instructions
/// (<see cref="PromptCacheHints.InstructionsBreakpoint"/>) move out of <see cref="ChatOptions.Instructions"/> into a
/// system message whose text carries the cache control, inserted after any leading system messages: that is where the
/// SDK would have appended the instructions, so the system prompt keeps its order. A hinted message
/// (<see cref="PromptCacheHints.Breakpoint"/>) is copied, and the last content the SDK would send as a block it marks
/// is replaced in the copy by a marked clone: in a system message the last text, blank or not; in any other message
/// the last non-blank <see cref="TextContent"/>, <see cref="FunctionCallContent"/> or
/// <see cref="FunctionResultContent"/>. Content the SDK sends verbatim from its raw representation is never chosen
/// (see <see cref="AnthropicCacheMarkers"/>). A message with no markable content gets no breakpoint and uses none of
/// the four.
/// </para>
/// <para>
/// <b>Cap.</b> When more than four hints are markable, the four with the highest priority win: the last hinted tool,
/// the instructions, the latest message, the message just before the first <see cref="PromptCacheHints.Transient"/>
/// message, then every other hinted message in list order, then every other hinted tool in list order. Markers the
/// caller set itself already count toward Anthropic's limit, so they reduce the four: those set through
/// <c>WithCacheControl</c> or a function's <c>CacheControl</c> property, and those inside raw SDK objects the SDK sends
/// verbatim, which are counted by serialising them with the SDK's converters. A
/// <see cref="ChatOptions.RawRepresentationFactory"/> is called once, with the SDK client as the SDK would call it; its
/// result is counted and handed on through a cloned options object whose factory returns that same instance.
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
internal sealed class AnthropicPromptCacheTranslator(IChatClient inner, CacheControlEphemeral? cacheControl) : DelegatingChatClient(inner)
{
    /// <summary>Anthropic's limit on <c>cache_control</c> blocks in one request.</summary>
    internal const int MaxBreakpoints = 4;

    /// <summary>The SDK 12.50.0 additional-count key for cache-write input tokens.</summary>
    internal const string SdkCacheWriteCountKey = "CacheCreationInputTokens";

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
        if (cacheControl is not { } control)
        {
            return (messages, options);
        }

        // The caller's raw request is built once: counted here and handed to the SDK as the same instance, which the SDK
        // clones before changing, so the caller's factory runs once per request as it would without this client.
        object? rawRequest = null;
        if (options?.RawRepresentationFactory is { } factory)
        {
            var captured = factory(InnerClient);
            rawRequest = captured;
            options = options.Clone();
            options.RawRepresentationFactory = _ => captured;
        }

        var list = messages as IList<ChatMessage> ?? messages.ToList();
        var plan = new Plan(list, options?.Tools, MaxBreakpoints - AnthropicCacheMarkers.CountCallerMarkers(list, options?.Tools, rawRequest));

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

        return (MarkMessages(list, plan.Messages, plan.Instructions ? options!.Instructions : null, control), MarkOptions(options, plan.Tools, plan.Instructions, control));
    }

    private static List<ChatMessage> MarkMessages(IList<ChatMessage> messages, Dictionary<int, int> marks, string? instructions, CacheControlEphemeral control)
    {
        var result = new List<ChatMessage>(messages.Count + 1);
        for (var i = 0; i < messages.Count; i++)
        {
            result.Add(marks.TryGetValue(i, out var content) ? Marked(messages[i], content, control) : messages[i]);
        }

        if (instructions is not null)
        {
            var leadingSystem = result.FindIndex(static m => m.Role != ChatRole.System);
            result.Insert(leadingSystem < 0 ? result.Count : leadingSystem, new ChatMessage(ChatRole.System, [new TextContent(instructions).WithCacheControl(control)]));
        }

        return result;
    }

    private static ChatOptions? MarkOptions(ChatOptions? options, List<int> toolIndices, bool movedInstructions, CacheControlEphemeral control)
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
            clone.Tools![index] = new CacheControlledFunction((AIFunction)clone.Tools[index], control);
        }

        return clone;
    }

    /// <summary>A shallow copy of <paramref name="message"/> whose content at <paramref name="contentIndex"/> is a marked clone.</summary>
    private static ChatMessage Marked(ChatMessage message, int contentIndex, CacheControlEphemeral control)
    {
        var copy = message.Clone();
        var contents = new List<AIContent>(message.Contents);
        contents[contentIndex] = CloneContent(contents[contentIndex]).WithCacheControl(control);
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
                && AnthropicCacheMarkers.MarkableContentIndex(messages[index]) is var content and >= 0)
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
                [AnthropicCacheMarkers.ToolKey] = cacheControl,
            };
    }
}
