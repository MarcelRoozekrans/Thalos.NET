using System.Collections.Frozen;
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

    private const string NameKey = "name";
    private const string DescriptionKey = "description";
    private const string TagsKey = "tags";
    private const string MarkdownExtension = ".md";

    private static readonly FrozenSet<string> ScalarKeys = new[] { NameKey, DescriptionKey }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> SequenceKeys = new[] { TagsKey }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenDictionary<string, string> Forbidden = FrozenDictionary<string, string>.Empty;

    /// <summary>Parses already-read <paramref name="text"/> as the skill named <paramref name="expectedName"/>; <paramref name="sourcePath"/> is the root-relative path used in error messages.</summary>
    public static Result<SkillDocument, AgentError> Parse(string sourcePath, string expectedName, string text, DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedName);
        ArgumentNullException.ThrowIfNull(text);

        var parsed = Frontmatter.Parse(text, ScalarKeys, SequenceKeys, Forbidden);
        if (parsed.IsFailure)
        {
            return Result<SkillDocument, AgentError>.Failure(AgentError.SkillValidationFailed($"{sourcePath}: {parsed.Error}"));
        }

        return Build(sourcePath, expectedName, parsed.Value, Hash(parsed.Value.Normalized), updatedAt);
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

    private static Result<SkillDocument, AgentError> Build(string sourcePath, string expectedName, Frontmatter.Parsed fm, string hash, DateTimeOffset updatedAt)
    {
        if (!fm.Scalars.TryGetValue(NameKey, out var rawName))
        {
            return Fail<SkillDocument>(sourcePath, "frontmatter is missing the required key 'name'");
        }

        if (!fm.Scalars.TryGetValue(DescriptionKey, out var description))
        {
            return Fail<SkillDocument>(sourcePath, "frontmatter is missing the required key 'description'");
        }

        if (!SkillName.TryParse(rawName, out var name))
        {
            return Fail<SkillDocument>(sourcePath, $"'{rawName}' is not a valid skill name (^[a-z][a-z0-9_-]{{0,63}}$)");
        }

        if (!string.Equals(name.Value, expectedName, StringComparison.Ordinal))
        {
            return Fail<SkillDocument>(sourcePath, $"frontmatter name '{name}' does not match the file or folder name '{expectedName}'");
        }

        var document = new SkillDocument
        {
            Name = name,
            Description = description.Trim(),
            Body = fm.Body,
            Tags = SkillRules.NormalizeTags(fm.Sequences.TryGetValue(TagsKey, out var tags) ? tags : null),
            SourcePath = sourcePath,
            ContentHash = hash,
            UpdatedAt = updatedAt,
        };

        return SkillRules.Validate(document) is { } error
            ? Fail<SkillDocument>(sourcePath, error.Message)
            : Result<SkillDocument, AgentError>.Success(document);
    }

    private static Result<T, AgentError> Fail<T>(string sourcePath, string reason) =>
        Result<T, AgentError>.Failure(AgentError.SkillValidationFailed($"{sourcePath}: {reason}"));
}
