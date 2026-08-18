using System.Globalization;
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
    private const string MarkdownExtension = ".md";

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

    /// <summary>
    /// Every skill file under <paramref name="root"/>, ordered by its root-relative path: <c>&lt;root&gt;/*.md</c> and
    /// <c>&lt;root&gt;/*/SKILL.md</c> — one level down only, deeper folders are ignored. A missing or unreadable root is a
    /// failure, not an exception, so one bad root cannot stop a sync; a sub-folder that cannot be listed costs only its own
    /// candidate file, which is returned anyway and reported as skipped when it is loaded.
    /// </summary>
    /// <remarks>
    /// Names are matched <b>case-sensitively on every OS</b> — the extension must be exactly <c>.md</c> and a folder skill's
    /// file exactly <c>SKILL.md</c>. Windows would happily match <c>skill.md</c> and <c>NOTES.MD</c> while Linux would not, so
    /// the stricter of the two is applied everywhere and a repository that loads on a developer's machine loads in CI. The
    /// order is taken from <see cref="RelativePath"/> rather than the full path for the same reason: <c>/</c> and <c>\</c> sort
    /// on opposite sides of the digits, so ordering full paths would disagree across platforms.
    /// </remarks>
    public static Result<IReadOnlyList<string>, AgentError> Enumerate(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var full = Path.GetFullPath(root);
        if (!Directory.Exists(full))
        {
            return Result<IReadOnlyList<string>, AgentError>.Failure(AgentError.SkillValidationFailed($"Skill root '{root}' does not exist."));
        }

        try
        {
            var found = new List<(string Relative, string Full)>();
            Collect(full, found);
            found.Sort(static (a, b) => string.CompareOrdinal(a.Relative, b.Relative));

            var files = new string[found.Count];
            for (var i = 0; i < files.Length; i++)
            {
                files[i] = found[i].Full;
            }

            return Result<IReadOnlyList<string>, AgentError>.Success(files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<IReadOnlyList<string>, AgentError>.Failure(AgentError.SkillValidationFailed($"Skill root '{root}' could not be read ({ex.GetType().Name})."));
        }
    }

    /// <summary>Reads and parses one file; the name is derived from its folder (<c>SKILL.md</c>) or its own file name.</summary>
    /// <remarks>Every foreseeable IO failure — a vanished file, a dangling symlink, a denied ACL, an over-long path — is a
    /// failure value naming the file, never an exception: one bad skill must not stop a host from starting.</remarks>
    public static async ValueTask<Result<SkillDocument, AgentError>> LoadAsync(string root, string filePath, DateTimeOffset updatedAt, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var sourcePath = RelativePath(root, filePath);
        if (ExpectedName(root, filePath) is not { } expected)
        {
            return Fail<SkillDocument>(sourcePath, "SKILL.md must live in a folder named after the skill");
        }

        if (expected.Length == 0)
        {
            return Fail<SkillDocument>(sourcePath, "the file or folder name is empty, so it names no skill");
        }

        try
        {
            var length = new FileInfo(filePath).Length;
            if (length > MaxFileBytes)
            {
                return Fail<SkillDocument>(sourcePath, string.Create(CultureInfo.InvariantCulture, $"the file is {length} bytes and was not read; the limit is {MaxFileBytes}"));
            }

            var text = await File.ReadAllTextAsync(filePath, Encoding.UTF8, ct).ConfigureAwait(false);
            return Parse(sourcePath, expected, text, updatedAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Fail<SkillDocument>(sourcePath, $"could not be read ({ex.GetType().Name})");
        }
    }

    /// <summary>The root-relative path with forward slashes, so error messages and <see cref="SkillDocument.SourcePath"/> read the same on every OS.</summary>
    public static string RelativePath(string root, string filePath) =>
        Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(filePath)).Replace('\\', '/');

    /// <summary>The skill name <paramref name="filePath"/> claims by its position, lower-cased; null when a <c>SKILL.md</c> sits directly in the root.</summary>
    internal static string? ExpectedName(string root, string filePath)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fileFull = Path.GetFullPath(filePath);
        var directory = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(fileFull) ?? "");
        string raw;
        if (string.Equals(Path.GetFileName(fileFull), SkillFileName, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(directory, rootFull, PathComparison))
            {
                return null;
            }

            raw = Path.GetFileName(directory);
        }
        else
        {
            raw = Path.GetFileNameWithoutExtension(fileFull);
        }

#pragma warning disable CA1308 // a skill name is a lower-case identifier, not user-facing text
        return raw.Trim().ToLowerInvariant();
#pragma warning restore CA1308
    }

    /// <summary>Windows paths are case-insensitive, Linux paths are not; this is filesystem truth, not a policy choice.</summary>
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static void Collect(string root, List<(string Relative, string Full)> found)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*" + MarkdownExtension, SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(Path.GetExtension(file), MarkdownExtension, StringComparison.Ordinal))
            {
                found.Add((RelativePath(root, file), file));
            }
        }

        foreach (var folder in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            CollectFolder(root, folder, found);
        }
    }

    /// <summary>
    /// One candidate sub-folder. A folder that cannot be listed — an ACL change, an unmounted share, an antivirus lock —
    /// yields its candidate <c>SKILL.md</c> path instead of propagating, so <see cref="LoadAsync"/> reports one skipped
    /// file. Letting it fail the whole root would be far worse: the sync then skips its deactivation sweep for every
    /// root, so one locked folder would stop the library updating at all.
    /// </summary>
    private static void CollectFolder(string root, string folder, List<(string Relative, string Full)> found)
    {
        try
        {
            foreach (var candidate in Directory.EnumerateFiles(folder, SkillFileName, SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(Path.GetFileName(candidate), SkillFileName, StringComparison.Ordinal))
                {
                    found.Add((RelativePath(root, candidate), candidate));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var candidate = Path.Combine(folder, SkillFileName);
            found.Add((RelativePath(root, candidate), candidate));
        }
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
