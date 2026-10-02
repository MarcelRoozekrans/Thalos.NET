using System.Text;

namespace Thalos.Mcp;

/// <summary>
/// Makes text a remote, untrusted peer wrote safe to log: control characters, which could forge log lines or terminal
/// escapes, become spaces, and the text is cut to a bounded length.
/// </summary>
internal static class LogSanitizer
{
    /// <summary>The longest text logged, before the ellipsis.</summary>
    public const int MaxLength = 200;

    /// <summary><paramref name="text"/> with every control character replaced by a space, cut to <paramref name="maxLength"/> characters plus an ellipsis.</summary>
    /// <param name="text">The untrusted text; null is empty.</param>
    /// <param name="maxLength">The longest text kept.</param>
    public static string Clean(string? text, int maxLength = MaxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var kept = Math.Min(text.Length, maxLength);
        var clean = new StringBuilder(kept + 1);
        for (var i = 0; i < kept; i++)
        {
            clean.Append(char.IsControl(text[i]) ? ' ' : text[i]);
        }

        if (text.Length > maxLength)
        {
            clean.Append('…');
        }

        return clean.ToString();
    }
}
