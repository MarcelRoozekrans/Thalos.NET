using System.Security.Cryptography;
using System.Text;
using ZeroAlloc.Results;

namespace Thalos.Skills;

/// <summary>
/// Reads <c>&lt;root&gt;/&lt;name&gt;/SKILL.md</c> and <c>&lt;root&gt;/&lt;name&gt;.md</c> into <see cref="SkillDocument"/>s.
/// </summary>
/// <remarks>
/// <para>
/// The frontmatter grammar is a deliberately strict subset of YAML rather than a YAML engine: three keys (<c>name</c>,
/// <c>description</c>, <c>tags</c>) at column 0, single-line scalars (plain, <c>'single'</c> or <c>"double"</c> quoted) and a
/// flow sequence for tags. Everything else — indentation, block scalars, anchors, block sequences, unknown or duplicate keys —
/// is a load error naming the file, so a malformed skill is never silently reinterpreted. Because tag items are split on
/// commas before they are unquoted, a comma inside a quoted tag surfaces as an unterminated-quote error rather than a wrong tag.
/// </para>
/// <para>Errors carry the root-relative source path and a reason; they never echo the file's contents.</para>
/// </remarks>
public static class SkillFileLoader
{
    /// <summary>Largest file the loader will read (a runaway file is rejected from its length, never loaded).</summary>
    public const int MaxFileBytes = 256 * 1024;

    /// <summary>The file name a skill folder must use.</summary>
    public const string SkillFileName = "SKILL.md";

    /// <summary>The UTF-8 byte-order mark, permitted (and ignored) at the very start of a skill file.</summary>
    private const char Bom = '\uFEFF';

    private const string ReservedScalarStarts = "|>&*!?%@{[`";
    private const string NameKey = "name";
    private const string DescriptionKey = "description";
    private const string TagsKey = "tags";

    private sealed record Frontmatter(string Text, string Body);

    private sealed record Entries(string? Name, string? Description, IReadOnlyList<string>? Tags);

    /// <summary>Parses already-read <paramref name="text"/> as the skill named <paramref name="expectedName"/>; <paramref name="sourcePath"/> is the root-relative path used in error messages.</summary>
    public static Result<SkillDocument, AgentError> Parse(string sourcePath, string expectedName, string text, DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedName);
        ArgumentNullException.ThrowIfNull(text);

        var normalized = text.TrimStart(Bom).ReplaceLineEndings("\n");
        var split = SplitFrontmatter(sourcePath, normalized);
        if (split.IsFailure)
        {
            return Result<SkillDocument, AgentError>.Failure(split.Error);
        }

        var entries = ParseEntries(sourcePath, split.Value.Text);
        return entries.IsFailure
            ? Result<SkillDocument, AgentError>.Failure(entries.Error)
            : Build(sourcePath, expectedName, entries.Value, split.Value.Body, Hash(normalized), updatedAt);
    }

    /// <summary>Lower-case hex SHA-256 of the LF-normalised file text.</summary>
    internal static string Hash(string normalizedText)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedText));
#pragma warning disable CA1308 // a hex digest is an identifier, not user-facing text
        return Convert.ToHexString(bytes).ToLowerInvariant();
#pragma warning restore CA1308
    }

    private static Result<Frontmatter, AgentError> SplitFrontmatter(string sourcePath, string normalized)
    {
        var lines = normalized.Split('\n');
        if (lines.Length == 0 || !string.Equals(lines[0], "---", StringComparison.Ordinal))
        {
            return Fail<Frontmatter>(sourcePath, "missing YAML frontmatter (the file must start with a `---` line)");
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
            return Fail<Frontmatter>(sourcePath, "unterminated YAML frontmatter (no closing `---` line)");
        }

        var body = string.Join('\n', lines[(close + 1)..]);
        if (body.StartsWith('\n'))
        {
            body = body[1..]; // exactly one blank line between the frontmatter and the body is conventional
        }

        return Result<Frontmatter, AgentError>.Success(new Frontmatter(string.Join('\n', lines[1..close]), body.TrimEnd()));
    }

    private static Result<Entries, AgentError> ParseEntries(string sourcePath, string frontmatter)
    {
        var entries = new Entries(null, null, null);
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
                // A block sequence under `tags:` is the one indented shape the grammar names explicitly (§0.6 rule 8);
                // its message tells the author what to write instead, so it wins over the generic indentation error.
                return Fail<Entries>(sourcePath, trimmed[0] == '-' && string.Equals(lastKey, TagsKey, StringComparison.Ordinal)
                    ? "tags must be a flow sequence, e.g. tags: [a, b]"
                    : "indented YAML is not supported in skill frontmatter");
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                return Fail<Entries>(sourcePath, "every frontmatter line must be `key: value`");
            }

            var key = line[..colon];
            if (!IsKey(key))
            {
                return Fail<Entries>(sourcePath, $"invalid frontmatter key '{key}' (keys match ^[a-z][a-z0-9_-]{{0,31}}$)");
            }

            if (!seen.Add(key))
            {
                return Fail<Entries>(sourcePath, $"duplicate frontmatter key '{key}'");
            }

            var applied = Apply(sourcePath, key, line[(colon + 1)..].TrimStart(' ', '\t'), entries);
            if (applied.IsFailure)
            {
                return applied;
            }

            entries = applied.Value;
            lastKey = key;
        }

        return Result<Entries, AgentError>.Success(entries);
    }

    private static Result<Entries, AgentError> Apply(string sourcePath, string key, string value, Entries entries)
    {
        if (string.Equals(key, TagsKey, StringComparison.Ordinal))
        {
            var parsed = ParseTags(sourcePath, value);
            return parsed.IsFailure
                ? Result<Entries, AgentError>.Failure(parsed.Error)
                : Result<Entries, AgentError>.Success(entries with { Tags = parsed.Value });
        }

        var isName = string.Equals(key, NameKey, StringComparison.Ordinal);
        if (!isName && !string.Equals(key, DescriptionKey, StringComparison.Ordinal))
        {
            return Fail<Entries>(sourcePath, $"unknown frontmatter key '{key}' (only name, description and tags are recognised)");
        }

        var scalar = ParseScalar(sourcePath, key, value);
        if (scalar.IsFailure)
        {
            return Result<Entries, AgentError>.Failure(scalar.Error);
        }

        return Result<Entries, AgentError>.Success(isName
            ? entries with { Name = scalar.Value }
            : entries with { Description = scalar.Value });
    }

    private static Result<string, AgentError> ParseScalar(string sourcePath, string key, string value)
    {
        if (value.Length == 0)
        {
            return Fail<string>(sourcePath, $"'{key}' has no value");
        }

        if (value[0] is '"' or '\'')
        {
            return Unquote(sourcePath, key, value, value[0]);
        }

        if (value.Contains(" #", StringComparison.Ordinal))
        {
            return Fail<string>(sourcePath, $"'{key}' is unquoted and contains a comment; quote the value");
        }

        return ReservedScalarStarts.Contains(value[0], StringComparison.Ordinal)
            ? Fail<string>(sourcePath, "block scalars, anchors and flow mappings are not supported in skill frontmatter")
            : Result<string, AgentError>.Success(value);
    }

    private static Result<string, AgentError> Unquote(string sourcePath, string key, string value, char quote)
    {
        if (value.Length < 2 || value[^1] != quote)
        {
            return Fail<string>(sourcePath, $"'{key}' has an unterminated quoted value");
        }

        var inner = value[1..^1];
        var sb = new StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (quote == '"' && c == '\\')
            {
                if (i + 1 >= inner.Length || inner[i + 1] is not ('"' or '\\'))
                {
                    return Fail<string>(sourcePath, $"'{key}' uses an unsupported escape (only \\\" and \\\\ are recognised)");
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

                return Fail<string>(sourcePath, $"'{key}' has an unescaped quote inside a quoted value");
            }

            sb.Append(c);
        }

        return Result<string, AgentError>.Success(sb.ToString());
    }

    private static Result<IReadOnlyList<string>, AgentError> ParseTags(string sourcePath, string value)
    {
        if (value.Length == 0)
        {
            return Result<IReadOnlyList<string>, AgentError>.Success([]);
        }

        if (value[0] != '[' || value[^1] != ']')
        {
            return Fail<IReadOnlyList<string>>(sourcePath, "tags must be a flow sequence, e.g. tags: [a, b]");
        }

        var inner = value[1..^1].Trim();
        if (inner.Length == 0)
        {
            return Result<IReadOnlyList<string>, AgentError>.Success([]);
        }

        if (inner.Contains('[', StringComparison.Ordinal) || inner.Contains(']', StringComparison.Ordinal))
        {
            return Fail<IReadOnlyList<string>>(sourcePath, "nested sequences are not supported in tags");
        }

        var items = inner.Split(',');
        var tags = new List<string>(items.Length);
        for (var i = 0; i < items.Length; i++)
        {
            var scalar = ParseScalar(sourcePath, TagsKey, items[i].Trim());
            if (scalar.IsFailure)
            {
                return Result<IReadOnlyList<string>, AgentError>.Failure(scalar.Error);
            }

            tags.Add(scalar.Value);
        }

        return Result<IReadOnlyList<string>, AgentError>.Success(tags);
    }

    private static Result<SkillDocument, AgentError> Build(string sourcePath, string expectedName, Entries entries, string body, string hash, DateTimeOffset updatedAt)
    {
        if (entries.Name is null)
        {
            return Fail<SkillDocument>(sourcePath, "frontmatter is missing the required key 'name'");
        }

        if (entries.Description is null)
        {
            return Fail<SkillDocument>(sourcePath, "frontmatter is missing the required key 'description'");
        }

        if (!SkillName.TryParse(entries.Name, out var name))
        {
            return Fail<SkillDocument>(sourcePath, $"'{entries.Name}' is not a valid skill name (^[a-z][a-z0-9_-]{{0,63}}$)");
        }

        if (!string.Equals(name.Value, expectedName, StringComparison.Ordinal))
        {
            return Fail<SkillDocument>(sourcePath, $"frontmatter name '{name}' does not match the file or folder name '{expectedName}'");
        }

        var document = new SkillDocument
        {
            Name = name,
            Description = entries.Description.Trim(),
            Body = body,
            Tags = SkillRules.NormalizeTags(entries.Tags),
            SourcePath = sourcePath,
            ContentHash = hash,
            UpdatedAt = updatedAt,
        };

        return SkillRules.Validate(document) is { } error
            ? Fail<SkillDocument>(sourcePath, error.Message)
            : Result<SkillDocument, AgentError>.Success(document);
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

    private static Result<T, AgentError> Fail<T>(string sourcePath, string reason) =>
        Result<T, AgentError>.Failure(AgentError.SkillValidationFailed($"{sourcePath}: {reason}"));
}
