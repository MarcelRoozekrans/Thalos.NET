using ZeroAlloc.Results;

namespace Thalos.Skills;

/// <summary>
/// The strict frontmatter grammar shared by every markdown document Thalos loads (skills, role charters): a deliberate
/// subset of YAML rather than a YAML engine. Three shapes only — single-line scalars (plain, <c>'single'</c> or
/// <c>"double"</c> quoted) and a flow sequence (<c>[a, b]</c>) at column 0 — plus <c>#</c> comments and blank lines.
/// Indentation, block scalars, anchors, block sequences, duplicate keys, unknown keys and keys the caller marks
/// forbidden are all load errors, so a malformed document is never silently reinterpreted. Because sequence items are
/// split on commas before they are unquoted, a comma inside a quoted item surfaces as an unterminated-quote error
/// rather than a wrong item.
/// </summary>
internal static class Frontmatter
{
    /// <summary>The frontmatter's parsed keys, the markdown body and the LF-normalised full file text (for hashing).</summary>
    internal readonly record struct Parsed(IReadOnlyDictionary<string, string> Scalars, IReadOnlyDictionary<string, IReadOnlyList<string>> Sequences, string Body, string Normalized);

    private readonly record struct Split(string Text, string Body);

    private const char Bom = '﻿';
    private const string ReservedScalarStarts = "|>&*!?%@{[`";

    /// <summary>Parses <paramref name="text"/>'s frontmatter and body against the given key grammar.</summary>
    /// <param name="text">The raw file text, exactly as read (BOM and any line ending style permitted).</param>
    /// <param name="scalarKeys">Keys allowed as single-line scalars.</param>
    /// <param name="sequenceKeys">Keys allowed as flow sequences.</param>
    /// <param name="forbidden">Keys that are rejected with a specific reason instead of "unknown key".</param>
    internal static Result<Parsed, string> Parse(
        string text,
        IReadOnlySet<string> scalarKeys,
        IReadOnlySet<string> sequenceKeys,
        IReadOnlyDictionary<string, string> forbidden)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(scalarKeys);
        ArgumentNullException.ThrowIfNull(sequenceKeys);
        ArgumentNullException.ThrowIfNull(forbidden);

        var normalized = text.TrimStart(Bom).ReplaceLineEndings("\n");
        var split = SplitFrontmatter(normalized);
        if (split.IsFailure)
        {
            return Result<Parsed, string>.Failure(split.Error);
        }

        var entries = ParseEntries(split.Value.Text, scalarKeys, sequenceKeys, forbidden);
        return entries.IsFailure
            ? Result<Parsed, string>.Failure(entries.Error)
            : Result<Parsed, string>.Success(new Parsed(entries.Value.Scalars, entries.Value.Sequences, split.Value.Body, normalized));
    }

    private static Result<Split, string> SplitFrontmatter(string normalized)
    {
        var lines = normalized.Split('\n');
        if (lines.Length == 0 || !string.Equals(lines[0], "---", StringComparison.Ordinal))
        {
            return Result<Split, string>.Failure("missing YAML frontmatter (the file must start with a `---` line)");
        }

        var close = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.Equals(lines[i], "---", StringComparison.Ordinal))
            {
                close = i;
                break;
            }
        }

        if (close < 0)
        {
            return Result<Split, string>.Failure("unterminated YAML frontmatter (no closing `---` line)");
        }

        var body = string.Join('\n', lines[(close + 1)..]);
        if (body.StartsWith('\n'))
        {
            body = body[1..]; // exactly one blank line between the frontmatter and the body is conventional
        }

        return Result<Split, string>.Success(new Split(string.Join('\n', lines[1..close]), body.TrimEnd()));
    }

    private sealed record Entries(Dictionary<string, string> Scalars, Dictionary<string, IReadOnlyList<string>> Sequences);

    private static Result<Entries, string> ParseEntries(string frontmatter, IReadOnlySet<string> scalarKeys, IReadOnlySet<string> sequenceKeys, IReadOnlyDictionary<string, string> forbidden)
    {
        var scalars = new Dictionary<string, string>(StringComparer.Ordinal);
        var sequences = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lastKey = "";

        foreach (var raw in frontmatter.Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.AsSpan().TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            if (line[0] is ' ' or '\t')
            {
                // A block sequence under a sequence key is the one indented shape the grammar names explicitly; its
                // message tells the author what to write instead, so it wins over the generic indentation error.
                return Result<Entries, string>.Failure(trimmed[0] == '-' && sequenceKeys.Contains(lastKey)
                    ? $"'{lastKey}' must be a flow sequence, e.g. {lastKey}: [a, b]"
                    : "indented YAML is not supported in frontmatter");
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                return Result<Entries, string>.Failure("every frontmatter line must be `key: value`");
            }

            var key = line[..colon];
            if (!IsKey(key))
            {
                return Result<Entries, string>.Failure($"invalid frontmatter key '{key}' (keys match ^[a-z][a-z0-9_-]{{0,31}}$)");
            }

            if (!seen.Add(key))
            {
                return Result<Entries, string>.Failure($"duplicate frontmatter key '{key}'");
            }

            var value = line[(colon + 1)..].TrimStart(' ', '\t');
            var error = Apply(key, value, scalarKeys, sequenceKeys, forbidden, scalars, sequences);
            if (error is not null)
            {
                return Result<Entries, string>.Failure(error);
            }

            lastKey = key;
        }

        return Result<Entries, string>.Success(new Entries(scalars, sequences));
    }

    /// <summary>Applies one <c>key: value</c> entry, mutating <paramref name="scalars"/> or <paramref name="sequences"/>; returns the failure reason, or null on success.</summary>
    private static string? Apply(
        string key,
        string value,
        IReadOnlySet<string> scalarKeys,
        IReadOnlySet<string> sequenceKeys,
        IReadOnlyDictionary<string, string> forbidden,
        Dictionary<string, string> scalars,
        Dictionary<string, IReadOnlyList<string>> sequences)
    {
        if (forbidden.TryGetValue(key, out var reason))
        {
            return reason;
        }

        if (sequenceKeys.Contains(key))
        {
            var parsed = ParseSequence(key, value);
            if (parsed.IsFailure)
            {
                return parsed.Error;
            }

            sequences[key] = parsed.Value;
            return null;
        }

        if (scalarKeys.Contains(key))
        {
            var parsed = ParseScalar(key, value);
            if (parsed.IsFailure)
            {
                return parsed.Error;
            }

            scalars[key] = parsed.Value;
            return null;
        }

        return $"unknown frontmatter key '{key}'";
    }

    private static Result<string, string> ParseScalar(string key, string value)
    {
        if (value.Length == 0)
        {
            return Result<string, string>.Failure($"'{key}' has no value");
        }

        if (value[0] is '"' or '\'')
        {
            return Unquote(key, value, value[0]);
        }

        if (value.Contains(" #", StringComparison.Ordinal))
        {
            return Result<string, string>.Failure($"'{key}' is unquoted and contains a comment; quote the value");
        }

        return ReservedScalarStarts.Contains(value[0], StringComparison.Ordinal)
            ? Result<string, string>.Failure("block scalars, anchors and flow mappings are not supported in frontmatter")
            : Result<string, string>.Success(value);
    }

    private static Result<string, string> Unquote(string key, string value, char quote)
    {
        if (value.Length < 2 || value[^1] != quote)
        {
            return Result<string, string>.Failure($"'{key}' has an unterminated quoted value");
        }

        var inner = value[1..^1];
        var sb = new System.Text.StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (quote == '"' && c == '\\')
            {
                if (i + 1 >= inner.Length || inner[i + 1] is not ('"' or '\\'))
                {
                    return Result<string, string>.Failure($"'{key}' uses an unsupported escape (only \\\" and \\\\ are recognised)");
                }

                sb.Append(inner[i + 1]);
                i++;
                continue;
            }

            if (c == quote)
            {
                if (quote == '\'' && i + 1 < inner.Length && inner[i + 1] == '\'')
                {
                    sb.Append('\'');
                    i++;
                    continue;
                }

                return Result<string, string>.Failure($"'{key}' has an unescaped quote inside a quoted value");
            }

            sb.Append(c);
        }

        return Result<string, string>.Success(sb.ToString());
    }

    private static Result<IReadOnlyList<string>, string> ParseSequence(string key, string value)
    {
        if (value.Length == 0)
        {
            return Result<IReadOnlyList<string>, string>.Success([]);
        }

        if (value[0] != '[' || value[^1] != ']')
        {
            return Result<IReadOnlyList<string>, string>.Failure($"'{key}' must be a flow sequence, e.g. {key}: [a, b]");
        }

        var inner = value[1..^1].Trim();
        if (inner.Length == 0)
        {
            return Result<IReadOnlyList<string>, string>.Success([]);
        }

        if (inner.Contains('[', StringComparison.Ordinal) || inner.Contains(']', StringComparison.Ordinal))
        {
            return Result<IReadOnlyList<string>, string>.Failure($"nested sequences are not supported in '{key}'");
        }

        var items = inner.Split(',');
        var list = new List<string>(items.Length);
        for (var i = 0; i < items.Length; i++)
        {
            var scalar = ParseScalar(key, items[i].Trim());
            if (scalar.IsFailure)
            {
                return Result<IReadOnlyList<string>, string>.Failure(scalar.Error);
            }

            list.Add(scalar.Value);
        }

        return Result<IReadOnlyList<string>, string>.Success(list);
    }

    private static bool IsKey(string key)
    {
        if (key.Length == 0 || key.Length > 32 || !char.IsAsciiLetterLower(key[0]))
        {
            return false;
        }

        foreach (var c in key)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c is not ('_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
