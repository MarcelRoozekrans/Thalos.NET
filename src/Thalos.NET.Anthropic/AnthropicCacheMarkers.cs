using Microsoft.Extensions.AI;

namespace Thalos.Anthropic;

/// <summary>
/// Which content of a message <see cref="AnthropicChatClient"/> sends as a block, so that
/// <see cref="AnthropicPromptCacheTranslator"/> marks one that reaches the wire.
/// </summary>
/// <remarks>
/// <b>System messages</b> send every <see cref="TextContent"/>, blank or not, and drop every other content type.
/// <b>Other messages</b> send non-blank <see cref="TextContent"/>, <see cref="FunctionCallContent"/> and
/// <see cref="FunctionResultContent"/>, and drop the rest; an assistant's text is trimmed at the end first, which drops
/// the same blank text.
/// </remarks>
internal static class AnthropicCacheMarkers
{
    /// <summary>The index of the last content in <paramref name="message"/> the adapter sends as a block, or -1.</summary>
    public static int MarkableContentIndex(ChatMessage message)
    {
        var system = message.Role == ChatRole.System;
        for (var i = message.Contents.Count - 1; i >= 0; i--)
        {
            var content = message.Contents[i];
            var markable = system
                ? content is TextContent
                : content is TextContent text ? !string.IsNullOrWhiteSpace(text.Text) : content is FunctionCallContent or FunctionResultContent;
            if (markable)
            {
                return i;
            }
        }

        return -1;
    }
}
