using Thalos.Git.Workspaces;
using Thalos.Workspaces;

namespace Thalos.Sandbox;

/// <summary>Configuration for run sandboxes.</summary>
public sealed class SandboxOptions
{
    /// <summary>The container image.</summary>
    public string Image { get; set; } = "";

    /// <summary>Per-sandbox resource limits.</summary>
    public SandboxLimits Limits { get; set; } = new();

    /// <summary>null = any extension, which is "*" on the wire. Only meaningful under a sandbox. Entries must not contain a semicolon or be "*".</summary>
    public IReadOnlySet<string>? AllowedWriteExtensions { get; set; }

    /// <summary>
    /// The protected path entries every sandboxed run gets, whatever <see cref="ProtectedPaths"/> adds (ruling R38): the
    /// git metadata and attribute files, and the CI definitions a pushed branch could run: <c>.git/</c>,
    /// <c>.gitattributes</c>, <c>.gitmodules</c>, <c>.github/</c>, <c>.gitlab-ci.yml</c>, <c>azure-pipelines.yml</c>,
    /// <c>.azure-pipelines/</c>, <c>.circleci/</c> and <c>Jenkinsfile</c>. A standing-instructions file such as
    /// <c>AGENT.md</c> is the host's to add.
    /// </summary>
    public static IReadOnlyList<string> DefaultProtectedPaths { get; } =
    [
        ".git/", ".gitattributes", ".gitmodules", ".github/", ".gitlab-ci.yml", "azure-pipelines.yml",
        ".azure-pipelines/", ".circleci/", "Jenkinsfile",
    ];

    /// <summary>
    /// Extra protected path entries, protected along with <see cref="DefaultProtectedPaths"/>; an entry that repeats a
    /// default, in any spelling <see cref="ProtectedPathSet"/> canonicalises to it, is protected once. Entries must not
    /// contain a semicolon or a <c>..</c> segment.
    /// </summary>
    public IList<string> ProtectedPaths { get; } = [];

    /// <summary>How long importing a workspace may take.</summary>
    public TimeSpan ImportTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long exporting a patch may take.</summary>
    public TimeSpan ExportTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long an exited sandbox restarted for its export may take to answer again.</summary>
    public TimeSpan RestartTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Bounds on an exported patch.</summary>
    public PatchApplyLimits PatchLimits { get; set; } = new();

    /// <summary>Trusted-side state: mirrors, records, stored patches, publish worktrees. Absolute.</summary>
    public string DataRoot { get; set; } = "";

    /// <summary>The set a sandboxed run is held to: <see cref="DefaultProtectedPaths"/>, then <see cref="ProtectedPaths"/>, de-duplicated.</summary>
    /// <exception cref="ArgumentException">An entry contains a <c>..</c> segment.</exception>
    internal ProtectedPathSet EffectiveProtectedPaths() => new([.. DefaultProtectedPaths, .. ProtectedPaths]);
}
