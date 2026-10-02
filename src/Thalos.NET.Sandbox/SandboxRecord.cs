using System.Text.Json.Serialization;

namespace Thalos.Sandbox;

/// <summary>Where a sandboxed run's record stands.</summary>
public enum SandboxRecordState
{
    /// <summary>Claimed by a create that has not finished: no container yet, or one still importing.</summary>
    Provisional,

    /// <summary>The sandbox imported the run's source; its tools are served.</summary>
    Ready,

    /// <summary>The run's patch is being exported before the container is parked.</summary>
    Exporting,

    /// <summary>The container is gone and the run's patch is stored on the trusted side.</summary>
    Parked,

    /// <summary>Being removed; nothing finds it any more.</summary>
    Removing,
}

/// <summary>Trusted-side record at &lt;DataRoot&gt;/sandboxes/&lt;run-id&gt;.json. Holds the bearer token; never inside a container.</summary>
/// <param name="RunId">The run.</param>
/// <param name="Repository">The repository name, the mirror's directory.</param>
/// <param name="Remote">The remote the mirror fetches.</param>
/// <param name="DefaultBranch">The repository's default branch.</param>
/// <param name="Branch">The run's branch, created in the sandbox.</param>
/// <param name="Solution">The solution, relative to the repository root.</param>
/// <param name="BaseCommit">The full sha the run started from.</param>
/// <param name="SandboxId">The sandbox's id.</param>
/// <param name="Token">The sandbox's bearer token: 32 random bytes as base64url, new for every sandbox.</param>
/// <param name="State">Where the record stands.</param>
/// <param name="CreatedAt">When the record was claimed, and again when it turned ready.</param>
/// <param name="PatchPath">Where the run's exported patch is stored, once parked.</param>
/// <param name="PatchMissing">The sandbox was gone before its patch was exported, so there is none to publish.</param>
/// <param name="ExportAttempts">
/// How many restart attempts parks have made to export the exited sandbox: counted before each is made, given back when
/// its caller cancels it. A park that dies midway leaves its attempt counted.
/// </param>
/// <param name="PatchMissingReason">Why there is no patch, when <paramref name="PatchMissing"/> is set.</param>
/// <param name="PatchApplied">
/// The stored patch was applied to the run's publish worktree, which a later checkout then returns as it is. A publish
/// worktree without it is one a checkout made but never finished, and is rebuilt.
/// </param>
public sealed record SandboxRecord(
    Guid RunId, string Repository, string Remote, string DefaultBranch, string Branch, string? Solution,
    string BaseCommit, string SandboxId, string Token, SandboxRecordState State, DateTimeOffset CreatedAt,
    string? PatchPath = null, bool PatchMissing = false, bool PatchApplied = false, int ExportAttempts = 0, string? PatchMissingReason = null)
{
    /// <summary>The token is never printed, so the record's generated text leaves it out.</summary>
    public override string ToString() => $"SandboxRecord {{ RunId = {RunId}, SandboxId = {SandboxId}, State = {State} }}";
}

/// <summary>A sandbox's answer to <c>GET /control/ready</c>.</summary>
/// <param name="Imported">Whether the run's commit is checked out.</param>
/// <param name="Restore"><c>pending</c>, <c>ok</c> or <c>failed</c>.</param>
/// <param name="RestoreDetail">The tail of the last restore's output, or why it did not run.</param>
/// <param name="Roslyn"><c>pending</c>, <c>ready</c> or <c>failed</c>.</param>
/// <param name="Detail">Why Roslyn failed, or null.</param>
public sealed record SandboxReadiness(bool Imported, string Restore, string? RestoreDetail, string Roslyn, string? Detail);

/// <summary>Source-generated serialization of the record, with the state as its name, and of the sandbox's readiness.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SandboxRecord))]
[JsonSerializable(typeof(SandboxReadiness))]
internal sealed partial class SandboxJsonContext : JsonSerializerContext;
