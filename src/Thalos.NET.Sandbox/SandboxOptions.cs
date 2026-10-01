using Thalos.Git.Workspaces;

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

    /// <summary>Extra protected path entries. Entries must not contain a semicolon.</summary>
    public IList<string> ProtectedPaths { get; } = [];

    /// <summary>How long importing a workspace may take.</summary>
    public TimeSpan ImportTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long exporting a patch may take.</summary>
    public TimeSpan ExportTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Bounds on an exported patch.</summary>
    public PatchApplyLimits PatchLimits { get; set; } = new();

    /// <summary>Trusted-side state: mirrors, records, stored patches, publish worktrees. Absolute.</summary>
    public string DataRoot { get; set; } = "";
}
