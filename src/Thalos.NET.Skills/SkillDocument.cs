using ZeroAlloc.Validation;

namespace Thalos.Skills;

/// <summary>
/// One procedure document. The files under <see cref="SkillOptions.Roots"/> are the source of truth; this is the synced
/// projection an agent's catalogue and <c>skills__load</c> read. Limits: see <see cref="SkillRules"/>. Record equality is
/// reference-based on <see cref="Tags"/> (a list); compare with <c>BeEquivalentTo</c> or by field in tests.
/// </summary>
[Validate]
public sealed record SkillDocument
{
    /// <summary>Maximum length of <see cref="Description"/>.</summary>
    public const int MaxDescriptionLength = 300;

    /// <summary>Maximum length of <see cref="Body"/> (64 Ki UTF-16 characters).</summary>
    public const int MaxBodyChars = 64 * 1024;

    /// <summary>Maximum number of <see cref="Tags"/>.</summary>
    public const int MaxTags = 10;

    /// <summary>Maximum length of one tag.</summary>
    public const int MaxTagLength = 32;

    /// <summary>Maximum length of <see cref="SourcePath"/>.</summary>
    public const int MaxSourcePathLength = 512;

    /// <summary>The skill's identity; also the file or folder it was loaded from.</summary>
    public required SkillName Name { get; init; }

    /// <summary>One line shown in every catalogue and in search results; at most <see cref="MaxDescriptionLength"/> characters.</summary>
    [NotEmpty] [MaxLength(MaxDescriptionLength)]
    public required string Description { get; init; }

    /// <summary>The procedure itself, verbatim from the file (line endings normalised to <c>\n</c>); at most <see cref="MaxBodyChars"/> characters.</summary>
    [NotEmpty] [MaxLength(MaxBodyChars)]
    public required string Body { get; init; }

    /// <summary>At most <see cref="MaxTags"/> tags of at most <see cref="MaxTagLength"/> characters, stored lower-case (<see cref="SkillRules.NormalizeTags"/>).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Root-relative path of the file this came from, for error messages (e.g. <c>release/SKILL.md</c>).</summary>
    [NotEmpty] [MaxLength(MaxSourcePathLength)]
    public required string SourcePath { get; init; }

    /// <summary>Lower-case hex SHA-256 of the LF-normalised file text; an unchanged hash lets the sync skip the file entirely.</summary>
    [NotEmpty]
    public required string ContentHash { get; init; }

    /// <summary>False once the file has disappeared from every root: the skill leaves the catalogues but its row survives.</summary>
    public bool IsActive { get; init; } = true;

    /// <summary>When the document was last written by a sync (from the host's <see cref="TimeProvider"/>).</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}
