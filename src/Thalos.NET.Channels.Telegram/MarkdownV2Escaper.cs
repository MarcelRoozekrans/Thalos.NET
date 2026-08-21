using System.Text;

namespace Thalos.Channels.Telegram;

/// <summary>
/// Escapes plain text so Telegram's Bot API accepts it as <c>MarkdownV2</c>, per the
/// <see href="https://core.telegram.org/bots/api#markdownv2-style">formatting rules</see> in the Bot API docs.
/// </summary>
/// <remarks>
/// Telegram parses the whole message as one MarkdownV2 document: a single unescaped reserved character, or a fenced
/// code block left open, makes it reject the entire <c>sendMessage</c>/<c>editMessageText</c> call with a 400 — the
/// reply is lost, not just malformed. Agent output is prose sprinkled with the exact characters MarkdownV2 reserves
/// (<c>.</c>, <c>!</c>, <c>-</c>, parentheses, ...), so escaping it correctly is a hard requirement, not cosmetics.
/// <para>
/// This escaper only recognizes triple-backtick fences (```` ``` ````), matching the one construct the rest of the
/// Telegram package emits deliberately (fenced code from tool output). It does not parse or preserve any other
/// MarkdownV2 entity — bold, italics, single-backtick inline code, and link syntax are treated as plain reserved
/// characters and escaped like everything else, which renders them as literal text rather than formatting. That is
/// intentional: this type has no way to know whether a bare <c>_</c> in agent output was meant as emphasis, so the
/// only safe behavior is to always print it literally.
/// </para>
/// </remarks>
public static class MarkdownV2Escaper
{
    private const string FenceMarker = "```";

    /// <summary>
    /// Escapes <paramref name="text"/> for Telegram's MarkdownV2 parse mode.
    /// </summary>
    /// <remarks>
    /// Outside a fenced code block, every MarkdownV2 reserved character
    /// (<c>_ * [ ] ( ) ~ ` &gt; # + - = | { } . !</c>) is prefixed with a backslash, per the Bot API's "in all other
    /// places" escaping rule. Inside a fenced code block, the content is left otherwise literal — per the Bot API's
    /// "inside <c>pre</c> and <c>code</c> entities" rule, only <c>\</c> and <c>`</c> are escaped there, since an
    /// unescaped one would terminate or corrupt the block. The ```` ``` ```` fence markers themselves, and any
    /// language tag immediately after an opening fence, are copied through unescaped so the fence still parses.
    /// If the input ends while a fence is still open — agent output can be truncated mid-block — a closing
    /// ```` ``` ```` is appended so the overall message still balances and Telegram does not reject it outright.
    /// </remarks>
    /// <param name="text">The plain text to escape. Must not be <see langword="null"/>.</param>
    /// <returns>
    /// <paramref name="text"/> with every character MarkdownV2 would otherwise misinterpret escaped, and any
    /// unclosed fence closed. Empty input is returned unchanged.
    /// </returns>
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        var insideFence = false;
        var i = 0;

        while (i < text.Length)
        {
            if (i + FenceMarker.Length <= text.Length &&
                text[i] == '`' && text[i + 1] == '`' && text[i + 2] == '`')
            {
                result.Append(FenceMarker);
                insideFence = !insideFence;
                i += FenceMarker.Length;
                continue;
            }

            var c = text[i];
            if (insideFence)
            {
                if (c is '\\' or '`')
                {
                    result.Append('\\');
                }

                result.Append(c);
            }
            else if (IsReserved(c))
            {
                result.Append('\\').Append(c);
            }
            else
            {
                result.Append(c);
            }

            i++;
        }

        if (insideFence)
        {
            result.Append(FenceMarker);
        }

        return result.ToString();
    }

    private static bool IsReserved(char c) =>
        c is '_' or '*' or '[' or ']' or '(' or ')' or '~' or '`' or '>' or '#' or '+' or '-' or '=' or '|' or '{' or '}' or '.' or '!';
}
