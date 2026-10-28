using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ZeroAlloc.Rest.Anthropic;

namespace Thalos.Anthropic;

/// <summary>
/// Maps Anthropic responses, stream events and usage onto M.E.AI types, the way the Anthropic SDK 13.3.0 chat client
/// did for text and tool use.
/// </summary>
/// <remarks>
/// <para>
/// <b>Content.</b> A text block becomes a <see cref="TextContent"/>, a tool-use block a <see cref="FunctionCallContent"/>
/// whose arguments are the input object's properties as <see cref="JsonElement"/> values; each keeps its block as
/// <see cref="AIContent.RawRepresentation"/>. A block of any other type is skipped.
/// </para>
/// <para>
/// <b>Usage.</b> <see cref="UsageDetails.InputTokenCount"/> is uncached plus cache-write plus cache-read input,
/// <see cref="UsageDetails.CachedInputTokenCount"/> the cache reads, <see cref="UsageDetails.TotalTokenCount"/> input
/// plus output, and a positive cache-write count goes under <see cref="AnthropicChatClient.CacheCreationInputTokensKey"/>.
/// </para>
/// <para>
/// <b>Finish reasons.</b> <c>end_turn</c> and <c>stop_sequence</c> are <see cref="ChatFinishReason.Stop"/>,
/// <c>max_tokens</c> and <c>model_context_window_exceeded</c> <see cref="ChatFinishReason.Length"/>, <c>tool_use</c>
/// <see cref="ChatFinishReason.ToolCalls"/>, <c>refusal</c> <see cref="ChatFinishReason.ContentFilter"/>; a blank
/// reason is <see cref="ChatFinishReason.Stop"/>, any other is passed on as its own value, and none is null.
/// </para>
/// </remarks>
internal static class AnthropicResponseMapper
{
    private const string ModelContextWindowExceeded = "model_context_window_exceeded";

    /// <summary>The response to a non-streaming request.</summary>
    /// <param name="response">What Anthropic answered.</param>
    /// <param name="requestModel">The model the request named, used when the response names none.</param>
    public static ChatResponse ToChatResponse(MessageResponse response, string requestModel)
    {
        var contents = new List<AIContent>(response.Content.Count);
        foreach (var block in response.Content)
        {
            if (ToContent(block) is { } content)
            {
                contents.Add(content);
            }
        }

        var message = new ChatMessage(ChatRole.Assistant, contents) { CreatedAt = DateTimeOffset.UtcNow, MessageId = response.Id };
        return new ChatResponse(message)
        {
            CreatedAt = message.CreatedAt,
            FinishReason = ToFinishReason(response.StopReason),
            ModelId = string.IsNullOrEmpty(response.Model) ? requestModel : response.Model,
            RawRepresentation = response,
            ResponseId = response.Id,
            Usage = response.Usage is { } usage
                ? ToUsageDetails(usage.InputTokens, usage.OutputTokens, usage.CacheCreationInputTokens, usage.CacheReadInputTokens)
                : null,
        };
    }

    /// <summary>The finish reason for an Anthropic <c>stop_reason</c>.</summary>
    public static ChatFinishReason? ToFinishReason(string? stopReason) => stopReason switch
    {
        null => null,
        StopReasons.EndTurn or StopReasons.StopSequence => ChatFinishReason.Stop,
        StopReasons.MaxTokens or ModelContextWindowExceeded => ChatFinishReason.Length,
        StopReasons.ToolUse => ChatFinishReason.ToolCalls,
        StopReasons.Refusal => ChatFinishReason.ContentFilter,
        _ when string.IsNullOrWhiteSpace(stopReason) => ChatFinishReason.Stop,
        _ => new ChatFinishReason(stopReason),
    };

    /// <summary>Usage with the cache split folded into the input count, as the SDK reported it.</summary>
    public static UsageDetails ToUsageDetails(int? inputTokens, int? outputTokens, int? cacheCreationInputTokens, int? cacheReadInputTokens)
    {
        var usage = new UsageDetails
        {
            InputTokenCount = Sum(Sum(cacheReadInputTokens, cacheCreationInputTokens), inputTokens),
            CachedInputTokenCount = cacheReadInputTokens,
            OutputTokenCount = outputTokens,
        };
        usage.TotalTokenCount = Sum(usage.InputTokenCount, usage.OutputTokenCount);
        if (cacheCreationInputTokens is > 0 and var writes)
        {
            (usage.AdditionalCounts ??= [])[AnthropicChatClient.CacheCreationInputTokensKey] = writes;
        }

        return usage;
    }

    private static long? Sum(long? a, long? b) => a is null && b is null ? null : (a ?? 0) + (b ?? 0);

    private static AIContent? ToContent(ContentBlock block) => block switch
    {
        TextBlock text => new TextContent(text.Text) { RawRepresentation = text },
        ToolUseBlock toolUse => Call(toolUse.Input, toolUse.Id, toolUse.Name, toolUse),
        _ => null,
    };

    private static FunctionCallContent Call(JsonElement input, string callId, string name, object rawRepresentation)
    {
        var call = FunctionCallContent.CreateFromParsedArguments(input, callId, name, static element => Arguments(element));
        call.RawRepresentation = rawRepresentation;
        return call;
    }

    /// <summary>The properties of a JSON object as <see cref="JsonElement"/> values.</summary>
    /// <exception cref="JsonException"><paramref name="input"/> is not an object.</exception>
    private static Dictionary<string, object?> Arguments(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"Tool input must be a JSON object, not {input.ValueKind}.");
        }

        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in input.EnumerateObject())
        {
            arguments[property.Name] = property.Value.Clone();
        }

        return arguments;
    }

    /// <summary>
    /// Turns the events of one stream into updates: one per event but <c>ping</c>, and after the last event one more
    /// holding the stream's single <see cref="UsageContent"/>.
    /// </summary>
    /// <remarks>
    /// Input and cache counts come from <c>message_start</c>, where Anthropic reports them; a <c>message_delta</c>
    /// count, cumulative, replaces the one it repeats. A tool call is yielded once, at its block's
    /// <c>content_block_stop</c>, with the concatenated <c>input_json_delta</c> fragments parsed; with no fragment text at
    /// all, the block's own <c>input</c> is used, so a call without input has empty arguments.
    /// </remarks>
    internal sealed class StreamState
    {
        private readonly Dictionary<int, StreamingCall> _calls = [];
        private string? _messageId;
        private string? _modelId;
        private ChatFinishReason? _finishReason;
        private int? _inputTokens;
        private int? _outputTokens;
        private int? _cacheCreationInputTokens;
        private int? _cacheReadInputTokens;
        private bool _hasUsage;

        /// <summary>The update for <paramref name="streamEvent"/>, or null for an event that yields none.</summary>
        public ChatResponseUpdate? Update(MessageStreamEvent streamEvent)
        {
            var contents = new List<AIContent>();
            switch (streamEvent)
            {
                case PingEvent:
                    return null;

                case MessageStartEvent start:
                    Start(start.Message);
                    break;

                case MessageDeltaEvent delta:
                    Delta(delta);
                    break;

                case ContentBlockStartEvent { ContentBlock: TextBlock text }:
                    contents.Add(new TextContent(text.Text) { RawRepresentation = text });
                    break;

                case ContentBlockStartEvent { ContentBlock: ToolUseBlock toolUse } blockStart:
                    _calls[blockStart.Index] = new StreamingCall(toolUse);
                    break;

                case ContentBlockDeltaEvent { Delta: TextDelta textDelta }:
                    contents.Add(new TextContent(textDelta.Text) { RawRepresentation = textDelta });
                    break;

                case ContentBlockDeltaEvent { Delta: InputJsonDelta inputDelta } blockDelta:
                    if (_calls.TryGetValue(blockDelta.Index, out var call))
                    {
                        call.Arguments.Append(inputDelta.PartialJson);
                    }

                    break;

                case ContentBlockStopEvent blockStop:
                    if (_calls.Remove(blockStop.Index, out var completed))
                    {
                        contents.Add(completed.ToContent());
                    }

                    break;
            }

            return new ChatResponseUpdate(ChatRole.Assistant, contents)
            {
                CreatedAt = DateTimeOffset.UtcNow,
                FinishReason = _finishReason,
                MessageId = _messageId,
                ModelId = _modelId,
                RawRepresentation = streamEvent,
                ResponseId = _messageId,
            };
        }

        private void Start(MessageResponse message)
        {
            _messageId ??= message.Id;
            _modelId ??= message.Model;
            if (message.Usage is { } usage)
            {
                _hasUsage = true;
                _inputTokens = usage.InputTokens;
                _outputTokens = usage.OutputTokens;
                _cacheCreationInputTokens = usage.CacheCreationInputTokens;
                _cacheReadInputTokens = usage.CacheReadInputTokens;
            }
        }

        /// <summary>The stop reason, and the usage counts the delta repeats; an omitted count keeps message_start's.</summary>
        private void Delta(MessageDeltaEvent delta)
        {
            _finishReason = ToFinishReason(delta.Delta.StopReason);
            if (delta.Usage is { } usage)
            {
                _hasUsage = true;
                _inputTokens = usage.InputTokens ?? _inputTokens;
                _outputTokens = usage.OutputTokens ?? _outputTokens;
                _cacheCreationInputTokens = usage.CacheCreationInputTokens ?? _cacheCreationInputTokens;
                _cacheReadInputTokens = usage.CacheReadInputTokens ?? _cacheReadInputTokens;
            }
        }

        /// <summary>The update holding the stream's usage, or null when the stream reported none.</summary>
        public ChatResponseUpdate? UsageUpdate() => _hasUsage
            ? new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(ToUsageDetails(_inputTokens, _outputTokens, _cacheCreationInputTokens, _cacheReadInputTokens))])
            {
                CreatedAt = DateTimeOffset.UtcNow,
                FinishReason = _finishReason,
                MessageId = _messageId,
                ModelId = _modelId,
                ResponseId = _messageId,
            }
            : null;

        private sealed class StreamingCall(ToolUseBlock block)
        {
            public StringBuilder Arguments { get; } = new();

            public FunctionCallContent ToContent()
            {
                if (Arguments.Length == 0)
                {
                    return Call(block.Input, block.Id, block.Name, block);
                }

                var call = FunctionCallContent.CreateFromParsedArguments(Arguments.ToString(), block.Id, block.Name, static json => Parse(json));
                call.RawRepresentation = block;
                return call;
            }

            private static Dictionary<string, object?> Parse(string json)
            {
                using var document = JsonDocument.Parse(json);
                return Arguments(document.RootElement);
            }
        }
    }
}
