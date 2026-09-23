using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using ZeroAlloc.Results;

namespace Thalos.Skills.Charters;

/// <summary>
/// Reads <c>&lt;root&gt;/&lt;role&gt;/CHARTER.md</c> and <c>&lt;root&gt;/&lt;role&gt;.md</c> into <see cref="RoleCharter"/>s.
/// </summary>
/// <remarks>
/// <para>
/// The frontmatter grammar (<see cref="Frontmatter"/>) allows four keys: <c>name</c>, <c>description</c> and
/// <c>model</c> as single-line scalars, <c>skills</c> as a flow sequence. <c>tools</c> is rejected with its own
/// message — a charter owns a role's prose, model and skills, but a role's tool list is its security envelope and
/// stays in configuration, where changing it needs a reviewed deploy. Any other key is rejected as unknown.
/// </para>
/// <para>Errors carry the root-relative source path and a reason; they never echo the file's contents.</para>
/// </remarks>
public static class RoleCharterFileLoader
{
    /// <summary>Largest file the loader will read (a runaway file is rejected from its length, never loaded).</summary>
    public const int MaxFileBytes = 256 * 1024;

    /// <summary>The file name a role folder must use.</summary>
    public const string CharterFileName = "CHARTER.md";

    private const string NameKey = "name";
    private const string DescriptionKey = "description";
    private const string ModelKey = "model";
    private const string SkillsKey = "skills";
    private const string MarkdownExtension = ".md";

    private static readonly IReadOnlyList<string> ScalarKeys = [NameKey, DescriptionKey, ModelKey];
    private static readonly IReadOnlyList<string> SequenceKeys = [SkillsKey];

    private static readonly FrozenDictionary<string, string> Forbidden = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["tools"] = "a charter may not name tools: a role's tool list is its security envelope and stays in configuration, where changing it needs a reviewed deploy",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Parses already-read <paramref name="text"/> as the role named <paramref name="expectedRole"/>; <paramref name="sourcePath"/> is the root-relative path used in error messages.</summary>
    public static Result<RoleCharter, AgentError> Parse(string sourcePath, string expectedRole, string text, DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRole);
        ArgumentNullException.ThrowIfNull(text);

        var parsed = Frontmatter.Parse(text, ScalarKeys, SequenceKeys, Forbidden, "role charter");
        if (parsed.IsFailure)
        {
            return Result<RoleCharter, AgentError>.Failure(AgentError.SkillValidationFailed($"{sourcePath}: {parsed.Error}"));
        }

        var fm = parsed.Value;
        if (!fm.Scalars.TryGetValue(NameKey, out var name) || !SkillName.TryParse(name, out var role) || !string.Equals(role.Value, expectedRole, StringComparison.Ordinal))
        {
            return Result<RoleCharter, AgentError>.Failure(AgentError.SkillValidationFailed($"{sourcePath}: name must be '{expectedRole}'"));
        }

        if (!fm.Scalars.TryGetValue(DescriptionKey, out var description) || string.IsNullOrWhiteSpace(description))
        {
            return Result<RoleCharter, AgentError>.Failure(AgentError.SkillValidationFailed($"{sourcePath}: description is required"));
        }

        if (string.IsNullOrWhiteSpace(fm.Body))
        {
            return Result<RoleCharter, AgentError>.Failure(AgentError.SkillValidationFailed($"{sourcePath}: the body is the role's instructions and must not be empty"));
        }

        return Result<RoleCharter, AgentError>.Success(new RoleCharter
        {
            Role = role.Value,
            Description = description.Trim(),
            Instructions = fm.Body,
            Model = fm.Scalars.TryGetValue(ModelKey, out var model) ? model : null,
            Skills = fm.Sequences.TryGetValue(SkillsKey, out var skills) ? skills : [],
            SourcePath = sourcePath,
            ContentHash = SkillFileLoader.Hash(fm.Normalized),
            UpdatedAt = updatedAt,
        });
    }

    /// <summary>
    /// Every charter file under <paramref name="root"/>, ordered by its root-relative path: <c>&lt;root&gt;/*.md</c> and
    /// <c>&lt;root&gt;/*/CHARTER.md</c> — one level down only, deeper folders are ignored. A missing or unreadable root is
    /// a failure, not an exception, so one bad root cannot stop a sync; a sub-folder that cannot be listed costs only its
    /// own candidate file, which is returned anyway and reported as skipped when it is loaded.
    /// </summary>
    /// <remarks>
    /// Names are matched <b>case-sensitively on every OS</b> — the extension must be exactly <c>.md</c> and a folder
    /// charter's file exactly <c>CHARTER.md</c>. The order is taken from <see cref="RelativePath"/> rather than the full
    /// path so Windows and Linux agree.
    /// </remarks>
    public static Result<IReadOnlyList<string>, AgentError> Enumerate(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var full = Path.GetFullPath(root);
        if (!Directory.Exists(full))
        {
            return Result<IReadOnlyList<string>, AgentError>.Failure(AgentError.SkillValidationFailed($"Role charter root '{root}' does not exist."));
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
            return Result<IReadOnlyList<string>, AgentError>.Failure(AgentError.SkillValidationFailed($"Role charter root '{root}' could not be read ({ex.GetType().Name})."));
        }
    }

    /// <summary>Reads and parses one file; the role is derived from its folder (<c>CHARTER.md</c>) or its own file name.</summary>
    /// <remarks>Every foreseeable IO failure — a vanished file, a dangling symlink, a denied ACL, an over-long path — is a
    /// failure value naming the file, never an exception: one bad charter must not stop a host from starting.</remarks>
    public static async ValueTask<Result<RoleCharter, AgentError>> LoadAsync(string root, string filePath, DateTimeOffset updatedAt, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var sourcePath = RelativePath(root, filePath);
        if (ExpectedRole(root, filePath) is not { } expected)
        {
            return Fail<RoleCharter>(sourcePath, "CHARTER.md must live in a folder named after the role");
        }

        if (expected.Length == 0)
        {
            return Fail<RoleCharter>(sourcePath, "the file or folder name is empty, so it names no role");
        }

        try
        {
            var length = new FileInfo(filePath).Length;
            if (length > MaxFileBytes)
            {
                return Fail<RoleCharter>(sourcePath, string.Create(CultureInfo.InvariantCulture, $"the file is {length} bytes and was not read; the limit is {MaxFileBytes}"));
            }

            var text = await File.ReadAllTextAsync(filePath, Encoding.UTF8, ct).ConfigureAwait(false);
            return Parse(sourcePath, expected, text, updatedAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Fail<RoleCharter>(sourcePath, $"could not be read ({ex.GetType().Name})");
        }
    }

    /// <summary>The root-relative path with forward slashes, so error messages and <see cref="RoleCharter.SourcePath"/> read the same on every OS.</summary>
    public static string RelativePath(string root, string filePath) =>
        Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(filePath)).Replace('\\', '/');

    /// <summary>The role name <paramref name="filePath"/> claims by its position, lower-cased; null when a <c>CHARTER.md</c> sits directly in the root.</summary>
    internal static string? ExpectedRole(string root, string filePath)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fileFull = Path.GetFullPath(filePath);
        var directory = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(fileFull) ?? "");
        string raw;
        if (string.Equals(Path.GetFileName(fileFull), CharterFileName, StringComparison.OrdinalIgnoreCase))
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

#pragma warning disable CA1308 // a role name is a lower-case identifier, not user-facing text
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
    /// yields its candidate <c>CHARTER.md</c> path instead of propagating, so <see cref="LoadAsync"/> reports one skipped
    /// file. Letting it fail the whole root would be far worse: the sync then skips its deactivation sweep for every
    /// root, so one locked folder would stop the library updating at all.
    /// </summary>
    private static void CollectFolder(string root, string folder, List<(string Relative, string Full)> found)
    {
        try
        {
            foreach (var candidate in Directory.EnumerateFiles(folder, CharterFileName, SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(Path.GetFileName(candidate), CharterFileName, StringComparison.Ordinal))
                {
                    found.Add((RelativePath(root, candidate), candidate));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var candidate = Path.Combine(folder, CharterFileName);
            found.Add((RelativePath(root, candidate), candidate));
        }
    }

    private static Result<T, AgentError> Fail<T>(string sourcePath, string reason) =>
        Result<T, AgentError>.Failure(AgentError.SkillValidationFailed($"{sourcePath}: {reason}"));
}
