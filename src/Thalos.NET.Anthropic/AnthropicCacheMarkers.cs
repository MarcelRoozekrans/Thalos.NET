using System.Text.Json;
using global::Anthropic.Models.Messages;
using Microsoft.Extensions.AI;

namespace Thalos.Anthropic;

/// <summary>
/// What the Anthropic SDK 12.50.0 chat client does with a request's content and tools, as far as <c>cache_control</c>
/// is concerned: which content it would send as a block <see cref="AnthropicPromptCacheTranslator"/> can mark, and how
/// many markers the caller already set through the SDK's own seams, counted the way they reach the wire.
/// </summary>
/// <remarks>
/// <para>
/// <b>System messages</b> send only text: content whose raw representation is a <see cref="TextBlockParam"/> is sent
/// verbatim, any other <see cref="TextContent"/> is mapped with its cache control, blank or not, and every other
/// content type is dropped. <b>Other messages</b> send content whose raw representation is a
/// <see cref="ContentBlockParam"/> verbatim; everything else is mapped, a raw <see cref="TextBlockParam"/> included,
/// with blank text dropped.
/// </para>
/// <para>
/// Markers inside raw SDK objects the SDK sends verbatim are counted by serialising those objects with the SDK's own
/// converters: raw content blocks, a tool made by <c>ToolUnion.AsAITool()</c>, and the body of the
/// <see cref="MessageCreateParams"/> a <see cref="ChatOptions.RawRepresentationFactory"/> returns, whose top-level
/// <c>cache_control</c> also takes a slot.
/// </para>
/// </remarks>
internal static class AnthropicCacheMarkers
{
    private const string CacheControlProperty = "cache_control";

    /// <summary>
    /// The content additional-property key <c>WithCacheControl</c> writes. The SDK keeps the constant private and its
    /// getter internal, so the key is read back from the public API once.
    /// </summary>
    internal static readonly string ContentKey =
        new TextContent("x").WithCacheControl(new CacheControlEphemeral()).AdditionalProperties!.Keys.Single();

    /// <summary>The tool additional-property key the SDK reads a function's cache control from.</summary>
    internal const string ToolKey = nameof(Tool.CacheControl);

    /// <summary>The index of the last content in <paramref name="message"/> the SDK would send as a block it marks, or -1.</summary>
    public static int MarkableContentIndex(ChatMessage message)
    {
        var system = message.Role == ChatRole.System;
        for (var i = message.Contents.Count - 1; i >= 0; i--)
        {
            var content = message.Contents[i];
            var markable = system
                ? content is TextContent && content.RawRepresentation is not TextBlockParam
                : content.RawRepresentation is not ContentBlockParam
                    && (content is TextContent text ? !string.IsNullOrWhiteSpace(text.Text) : content is FunctionCallContent or FunctionResultContent);
            if (markable)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Markers the caller set itself, through the SDK's seams or inside raw SDK objects, that will reach the wire.</summary>
    public static int CountCallerMarkers(IList<ChatMessage> messages, ChatOptions? options, IChatClient sdkClient)
    {
        var count = 0;
        foreach (var message in messages)
        {
            var system = message.Role == ChatRole.System;
            foreach (var content in message.Contents)
            {
                count += system ? CountInSystemContent(content) : CountInContent(content);
            }
        }

        foreach (var tool in options?.Tools ?? [])
        {
            count += CountInTool(tool);
        }

        if (options?.RawRepresentationFactory?.Invoke(sdkClient) is MessageCreateParams raw)
        {
            count += Count(JsonSerializer.SerializeToElement(raw.RawBodyData));
        }

        return count;
    }

    private static int CountInSystemContent(AIContent content) => content switch
    {
        { RawRepresentation: TextBlockParam raw } => Count(JsonSerializer.SerializeToElement(raw)),
        TextContent => HasContentKey(content),
        _ => 0,
    };

    private static int CountInContent(AIContent content) => content switch
    {
        { RawRepresentation: ContentBlockParam raw } => Count(JsonSerializer.SerializeToElement(raw)),
        TextContent text when string.IsNullOrWhiteSpace(text.Text) => 0,
        _ => HasContentKey(content),
    };

    private static int CountInTool(AITool tool) => tool.GetService(typeof(ToolUnion)) is ToolUnion raw
        ? Count(JsonSerializer.SerializeToElement(raw))
        : tool is AIFunctionDeclaration && tool.AdditionalProperties.TryGetValue(ToolKey, out var value) && value is CacheControlEphemeral ? 1 : 0;

    private static int HasContentKey(AIContent content) =>
        content.AdditionalProperties?.TryGetValue(ContentKey, out var value) == true && value is CacheControlEphemeral ? 1 : 0;

    private static int Count(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Sum(static p => (p.NameEquals(CacheControlProperty) && p.Value.ValueKind == JsonValueKind.Object ? 1 : 0) + Count(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Sum(Count),
        _ => 0,
    };
}
