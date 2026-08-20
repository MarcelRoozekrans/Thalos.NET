namespace Thalos.Channels.Telegram;

/// <summary>
/// Splits already-escaped MarkdownV2 text into chunks that each fit within Telegram's per-message character cap.
/// </summary>
/// <remarks>
/// Telegram's <c>sendMessage</c> rejects any text over 4096 UTF-16 characters with a 400, so a long agent reply has
/// to be broken into several messages. This type performs that split on the text <see cref="MarkdownV2Escaper"/>
/// already produced.
/// <para>
/// A naive split is not safe here: <see cref="MarkdownV2Escaper"/> only balances ```` ``` ```` fences across the
/// whole input, so if a chunk boundary falls inside a fenced code block, the chunk that ends there is left with an
/// unclosed fence and the chunk that follows begins with an orphaned closing fence — Telegram rejects both, and the
/// reply is lost. <see cref="Split"/> tracks fence state the same way <see cref="MarkdownV2Escaper"/> does (a bare
/// ```` ``` ```` toggles fence state; nothing else is fence syntax) and, when a boundary would land inside a fence,
/// closes the fence in the chunk being emitted and reopens it — with the same language tag — at the start of the
/// next chunk. Both the appended closer and the reopener are real characters and are counted against the limit.
/// </para>
/// </remarks>
public static class MessageSplitter
{
    private const string FenceMarker = "```";

    /// <summary>
    /// Splits <paramref name="text"/> into chunks that each fit within <paramref name="limit"/> characters.
    /// </summary>
    /// <remarks>
    /// Each chunk is chosen greedily: while the remaining text would exceed <paramref name="limit"/>, the last
    /// paragraph break (<c>"\n\n"</c>) at or before the limit is used as the boundary; failing that, the last line
    /// break (<c>"\n"</c>); failing that, the text is cut at exactly the limit. The separator itself is dropped
    /// from the boundary — it appears in neither chunk — but is never trimmed from inside a chunk. A run of text
    /// with no boundary at all (a single very long line) is still split, never dropped and never emitted
    /// over-length.
    /// <para>
    /// A boundary that falls inside a fenced code block is adjusted so no chunk is ever malformed: the chunk being
    /// emitted gets a closing fence appended, and the next chunk opens with a fence carrying the same language tag
    /// the source used. Both additions count toward <paramref name="limit"/>, so the content boundary is chosen to
    /// leave room for them rather than overflowing the limit.
    /// </para>
    /// </remarks>
    /// <param name="text">The already-escaped text to split. Must not be <see langword="null"/>.</param>
    /// <param name="limit">
    /// The maximum length of each returned chunk, in UTF-16 characters. Defaults to Telegram's 4096-character cap.
    /// Must be large enough to hold a reopened fence (the marker, language tag, and a newline) plus at least one
    /// character of content; the default and Telegram's own limits comfortably satisfy this for any realistic
    /// language tag.
    /// </param>
    /// <returns>
    /// The chunks of <paramref name="text"/>, in order, each no longer than <paramref name="limit"/>. Empty input
    /// yields an empty list, so callers send nothing rather than an empty message.
    /// </returns>
    public static IReadOnlyList<string> Split(string text, int limit = 4096)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limit, 0);

        if (text.Length == 0)
        {
            return [];
        }

        var chunks = new List<string>();
        var pos = 0;
        var insideFence = false;
        string? fenceLang = null;

        while (pos < text.Length)
        {
            var prefix = insideFence ? FenceMarker + fenceLang + "\n" : string.Empty;
            var remaining = text.Length - pos;

            if (prefix.Length + remaining <= limit)
            {
                chunks.Add(prefix + text[pos..]);
                break;
            }

            var avail = limit - prefix.Length;
            var (contentEnd, nextStart) = FindBoundary(text, pos, pos + avail);
            var (stillInside, lang) = ScanFenceState(text, pos, contentEnd, insideFence, fenceLang);

            var closer = string.Empty;
            if (stillInside)
            {
                // The boundary found above lands inside a fence: reserve room for a closing fence and re-derive
                // the boundary within the smaller budget so the appended closer never pushes the chunk over limit.
                avail = limit - prefix.Length - FenceMarker.Length;
                (contentEnd, nextStart) = FindBoundary(text, pos, pos + avail);
                (stillInside, lang) = ScanFenceState(text, pos, contentEnd, insideFence, fenceLang);
                closer = stillInside ? FenceMarker : string.Empty;
            }

            chunks.Add(prefix + text[pos..contentEnd] + closer);

            insideFence = stillInside;
            fenceLang = stillInside ? lang : null;
            pos = nextStart;
        }

        return chunks;
    }

    /// <summary>
    /// Finds the boundary for a chunk covering <c>text[start..end)</c>, preferring the last paragraph break, then
    /// the last line break, then a hard cut at <paramref name="end"/>. Returns the exclusive end of the chunk's
    /// content and the index the next chunk should resume at (past any dropped separator).
    /// </summary>
    private static (int ContentEnd, int NextStart) FindBoundary(string text, int start, int end)
    {
        var paragraphAt = text.LastIndexOf("\n\n", end - 1, end - start, StringComparison.Ordinal);
        if (paragraphAt >= start)
        {
            return (paragraphAt, paragraphAt + 2);
        }

        var lineAt = text.LastIndexOf('\n', end - 1, end - start);
        if (lineAt >= start)
        {
            return (lineAt, lineAt + 1);
        }

        var cut = end;

        // A raw (unescaped) run of backtick characters can only be a fence marker — MarkdownV2Escaper escapes
        // every other backtick — so a hard cut must never land inside one; doing so would corrupt the marker for
        // both chunks. Back the cut up to the start of the run instead, keeping at least one character of content.
        while (cut > start + 1 && text[cut - 1] == '`' && text[cut] == '`')
        {
            cut--;
        }

        return (cut, cut);
    }

    /// <summary>
    /// Computes fence state after scanning <c>text[start..end)</c>, starting from <paramref name="initialInside"/>
    /// / <paramref name="initialLang"/>. Mirrors <see cref="MarkdownV2Escaper"/>: a bare ```` ``` ```` toggles
    /// fence state, and nothing else is fence syntax.
    /// </summary>
    private static (bool Inside, string? Lang) ScanFenceState(string text, int start, int end, bool initialInside, string? initialLang)
    {
        var inside = initialInside;
        var lang = initialLang;
        var i = start;

        while (i + FenceMarker.Length <= end)
        {
            if (text[i] == '`' && text[i + 1] == '`' && text[i + 2] == '`')
            {
                inside = !inside;
                i += FenceMarker.Length;

                if (inside)
                {
                    lang = ReadLanguageTag(text, i);
                }

                continue;
            }

            i++;
        }

        return (inside, lang);
    }

    /// <summary>
    /// Reads the language tag that follows an opening fence marker at <paramref name="tagStart"/>: everything up
    /// to the next line break or the next fence marker, whichever comes first.
    /// </summary>
    private static string ReadLanguageTag(string text, int tagStart)
    {
        var i = tagStart;

        while (i < text.Length && text[i] != '\n' &&
               !(i + FenceMarker.Length <= text.Length && text[i] == '`' && text[i + 1] == '`' && text[i + 2] == '`'))
        {
            i++;
        }

        return text[tagStart..i];
    }
}
