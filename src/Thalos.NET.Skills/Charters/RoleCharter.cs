namespace Thalos.Skills.Charters;

/// <summary>
/// One role's prose: who it is, how it thinks, what model it prefers and which skills it may reach for. A charter
/// never names tools — a role's tool list is its security envelope and stays in configuration, where changing it
/// needs a reviewed deploy (see <see cref="RoleCharterFileLoader"/>). The file is the source of truth; this is the
/// synced projection an <see cref="IRoleCharterStore"/> holds.
/// </summary>
public sealed record RoleCharter
{
    /// <summary>The role's identity; also the file or folder it was loaded from (frontmatter <c>name</c>).</summary>
    public required string Role { get; init; }

    /// <summary>One line describing the role.</summary>
    public required string Description { get; init; }

    /// <summary>The charter's prose — the markdown body, verbatim (line endings normalised to <c>\n</c>).</summary>
    public required string Instructions { get; init; }

    /// <summary>The role's preferred model, or null to use the host's default.</summary>
    public string? Model { get; init; }

    /// <summary>Skill names this role may load, in the order the frontmatter lists them.</summary>
    public IReadOnlyList<string> Skills { get; init; } = [];

    /// <summary>Root-relative path of the file this came from, for error messages (e.g. <c>reviewer/CHARTER.md</c>).</summary>
    public required string SourcePath { get; init; }

    /// <summary>Lower-case hex SHA-256 of the LF-normalised file text; an unchanged hash lets a sync skip the file entirely.</summary>
    public required string ContentHash { get; init; }

    /// <summary>False once this version is no longer the role's current one, or the role has disappeared from every root.</summary>
    public bool IsActive { get; init; } = true;

    /// <summary>When this version was last written by a sync (from the host's <see cref="TimeProvider"/>).</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}
