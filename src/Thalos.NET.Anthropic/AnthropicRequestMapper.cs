using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ZeroAlloc.Rest.Anthropic;

namespace Thalos.Anthropic;

/// <summary>
/// Maps an M.E.AI request onto a <see cref="MessageRequest"/>, the way the Anthropic SDK 13.3.0 chat client did for the
/// content Thalos sends: text, function calls and function results, function tools, and the cache breakpoints the
/// prompt-cache translator places.
/// </summary>
/// <remarks>
/// <para>
/// <b>System prompt.</b> The text of every system message, blank text included, becomes a system block in message
/// order, followed by <see cref="ChatOptions.Instructions"/>. Other system content is dropped. A system message after
/// the conversation has started joins the system prompt too: the request model has no system role for a message.
/// </para>
/// <para>
/// <b>Messages.</b> An assistant message maps to the assistant role, every other role to the user role. Text is sent
/// unless blank, and assistant text is trimmed at the end first. A <see cref="FunctionCallContent"/> becomes a
/// <c>tool_use</c> block, a <see cref="FunctionResultContent"/> a <c>tool_result</c> block, marked as an error when it
/// carries an exception. Any other content is dropped, and a message left with no blocks is not sent. With no message at
/// all, one user message holding a zero-width space is sent, as Anthropic requires at least one.
/// </para>
/// <para>
/// <b>Options.</b> Model and max tokens fall back to the client's defaults; temperature, top-p and stop sequences are
/// sent when set. Of the tools, only an <see cref="AIFunctionDeclaration"/> is sent, with its JSON schema as it is.
/// Raw representations, a <see cref="ChatOptions.RawRepresentationFactory"/> and every other option are ignored.
/// </para>
/// <para>
/// <b>Breakpoints.</b> A text, function-call or function-result content, and a tool, whose additional properties hold a
/// <see cref="CacheBreakpoint"/> under <see cref="AnthropicChatClient.CacheBreakpointKey"/> is sent with its
/// <c>cache_control</c>. Nothing else reaches <c>cache_control</c>.
/// </para>
/// <para>
/// Values that are not already JSON are serialized through <see cref="AIJsonUtilities.DefaultOptions"/>' type
/// information for <see cref="object"/>, as the SDK did; there is no reflection-based serialization here.
/// </para>
/// </remarks>
internal static class AnthropicRequestMapper
{
    /// <summary>What the SDK sent when a request had no message to send.</summary>
    private const string ZeroWidthSpace = "\u200b";

    /// <summary>Maps <paramref name="messages"/> and <paramref name="options"/> onto a request.</summary>
    /// <param name="messages">The conversation.</param>
    /// <param name="options">The options, if any.</param>
    /// <param name="defaultModel">The model when <see cref="ChatOptions.ModelId"/> is not set.</param>
    /// <param name="defaultMaxTokens">The max tokens when <see cref="ChatOptions.MaxOutputTokens"/> is not set.</param>
    public static MessageRequest Map(IEnumerable<ChatMessage> messages, ChatOptions? options, string defaultModel, int defaultMaxTokens)
    {
        ArgumentNullException.ThrowIfNull(messages);
        List<TextBlockParam>? system = null;
        var mapped = new List<MessageParam>();
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                foreach (var text in message.Contents.OfType<TextContent>())
                {
                    (system ??= []).Add(new TextBlockParam(text.Text, BreakpointOf(text.AdditionalProperties)));
                }

                continue;
            }

            var blocks = Blocks(message);
            if (blocks.Count > 0)
            {
                mapped.Add(new MessageParam(message.Role == ChatRole.Assistant ? MessageRole.Assistant : MessageRole.User, blocks));
            }
        }

        if (options?.Instructions is { } instructions)
        {
            (system ??= []).Add(new TextBlockParam(instructions));
        }

        if (mapped.Count == 0)
        {
            mapped.Add(new MessageParam(MessageRole.User, [new TextBlockParam(ZeroWidthSpace)]));
        }

        return new MessageRequest
        {
            Model = options?.ModelId ?? defaultModel,
            MaxTokens = options?.MaxOutputTokens ?? defaultMaxTokens,
            System = system is null ? default(SystemPrompt?) : SystemPrompt.FromBlocks(system),
            Messages = mapped,
            Tools = Tools(options?.Tools),
            Temperature = options?.Temperature,
            TopP = options?.TopP,
            StopSequences = options?.StopSequences is { Count: > 0 } stop ? [.. stop] : null,
        };
    }

    private static List<ContentBlockParam> Blocks(ChatMessage message)
    {
        var blocks = new List<ContentBlockParam>(message.Contents.Count);
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    var value = message.Role == ChatRole.Assistant ? text.Text.TrimEnd() : text.Text;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        blocks.Add(new TextBlockParam(value, BreakpointOf(text.AdditionalProperties)));
                    }

                    break;

                case FunctionCallContent call:
                    blocks.Add(new ToolUseBlockParam(call.CallId, call.Name, Input(call.Arguments), BreakpointOf(call.AdditionalProperties)));
                    break;

                case FunctionResultContent result:
                    blocks.Add(new ToolResultBlockParam(
                        result.CallId, ResultText(result.Result), result.Exception is null ? null : true, BreakpointOf(result.AdditionalProperties)));
                    break;
            }
        }

        return blocks;
    }

    private static List<ToolDefinition>? Tools(IList<AITool>? tools)
    {
        List<ToolDefinition>? mapped = null;
        foreach (var tool in tools ?? [])
        {
            if (tool is AIFunctionDeclaration function)
            {
                (mapped ??= []).Add(new ToolDefinition(function.Name, function.Description, function.JsonSchema, BreakpointOf(function.AdditionalProperties)));
            }
        }

        return mapped;
    }

    /// <summary>The cache control of the breakpoint the translator placed, or null; any other value is ignored.</summary>
    private static CacheControl? BreakpointOf(IReadOnlyDictionary<string, object?>? properties) =>
        properties is not null && properties.TryGetValue(AnthropicChatClient.CacheBreakpointKey, out var value) && value is CacheBreakpoint breakpoint
            ? breakpoint.Control
            : null;

    /// <summary>The arguments as one JSON object; a value that is not a <see cref="JsonElement"/> is serialized.</summary>
    private static JsonElement Input(IDictionary<string, object?>? arguments)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in arguments ?? new Dictionary<string, object?>(StringComparer.Ordinal))
            {
                writer.WritePropertyName(name);
                WriteValue(writer, value);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        if (value is JsonElement element)
        {
            element.WriteTo(writer);
        }
        else
        {
            JsonSerializer.Serialize(writer, value, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(object)));
        }
    }

    /// <summary>
    /// A tool result as the text the request model carries: a string as it is, text content as its text, several
    /// contents one per line, anything else as its JSON. The SDK sent content as blocks; the request model holds a string.
    /// </summary>
    private static string ResultText(object? result) => result switch
    {
        string text => text,
        AIContent content => ContentText(content),
        IEnumerable<AIContent> contents => string.Join('\n', contents.Select(ContentText)),
        _ => Json(result),
    };

    private static string ContentText(AIContent content) => content is TextContent text ? text.Text : Json(content);

    private static string Json(object? value) => JsonSerializer.Serialize(value, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(object)));
}
