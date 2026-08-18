using System.Globalization;
using System.Text.RegularExpressions;

namespace Thalos.Skills;

/// <summary>The delimiters skills are wrapped in, and the neutralisation that stops skill text closing or forging them, or the memory block's.</summary>
internal static partial class SkillBlock
{
    /// <summary>Opening tag of the catalogue block injected into every turn.</summary>
    public const string CatalogueOpen = "<skills note=\"procedures you may load with skills__load\">";

    /// <summary>Closing tag of the catalogue block.</summary>
    public const string CatalogueClose = "</skills>";

    /// <summary>Closing tag of one loaded skill.</summary>
    public const string SkillClose = "</skill>";

    /// <summary>The opening tag of one loaded skill.</summary>
    public static string SkillOpen(SkillName name) => "<skill name=\"" + name.Value + "\">";

    /// <summary>The "… and N more" line appended when the catalogue does not fit its budget.</summary>
    public static string Overflow(int remaining) => string.Create(CultureInfo.InvariantCulture, $"… and {remaining} more (use skills__search)");

    /// <summary>One line: line endings collapse to spaces and no skill, skills or memories tag can be produced from the text.</summary>
    public static string SanitizeLine(string text) => Neutralize(text.ReplaceLineEndings(" ")).Trim();

    /// <summary>Multi-line: the text is kept verbatim (line endings normalised to <c>\n</c>) but no skill, skills or memories tag can be produced from it.</summary>
    public static string SanitizeBody(string text) => Neutralize(text.ReplaceLineEndings("\n"));

    private static string Neutralize(string text) => SkillTag().Replace(text, static m => string.Concat("&lt;", m.ValueSpan[1..]));

    // Escapes the '<' of every opening or closing spelling of <skill> / <skills> / <memories> (any casing, whitespace
    // around the slash), keeping the rest verbatim. Same shape and same case-insensitivity as MemoryRecallBlock's tag
    // guard, and it covers the memory tags for the same reason that one covers these: both blocks land in the same
    // ChatOptions.Instructions, so text neutralised for one of them could otherwise forge the other.
    // The word boundary keeps the escape to real tags: "<skillset" and "<memoriesX" are ordinary words.
    [GeneratedRegex(@"<\s*/?\s*(?:memories|skills?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)] // MA0009: timeout (bodies are ≤ 64 Ki chars; the pattern is linear)
    private static partial Regex SkillTag();
}
