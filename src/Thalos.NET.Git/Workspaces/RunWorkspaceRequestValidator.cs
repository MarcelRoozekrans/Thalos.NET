using Thalos.Workspaces;

namespace Thalos.Git.Workspaces;

/// <summary>
/// The checks every mirror-backed provider applies to a <see cref="RunWorkspaceRequest"/> before any git runs: shared by
/// <see cref="GitWorktreeWorkspaceProvider"/> and the sandbox provider, so the two cannot drift apart.
/// </summary>
internal static class RunWorkspaceRequestValidator
{
    /// <summary>Why <paramref name="request"/> cannot be used, or null when it can.</summary>
    /// <param name="request">The request.</param>
    public static AgentError? Validate(RunWorkspaceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!GitMirrorStore.IsValidRepositoryName(request.Repository))
        {
            return AgentError.Validation($"Repository '{request.Repository}' is not a valid mirror directory name.");
        }

        if (string.IsNullOrWhiteSpace(request.Remote))
        {
            return AgentError.Validation("Remote must not be blank.");
        }

        if (request.Remote.StartsWith('-'))
        {
            return AgentError.Validation($"Remote '{request.Remote}' must not start with '-'.");
        }

        if (string.IsNullOrWhiteSpace(request.DefaultBranch))
        {
            return AgentError.Validation("DefaultBranch must not be blank.");
        }

        if (string.IsNullOrWhiteSpace(request.Branch))
        {
            return AgentError.Validation("Branch must not be blank.");
        }

        if (request.Branch.StartsWith('-'))
        {
            return AgentError.Validation($"Branch '{request.Branch}' must not start with '-'.");
        }

        if (request.StartPoint is not null && !GitMirrorStore.IsFullSha(request.StartPoint))
        {
            return AgentError.Validation("StartPoint must be a full 40-character commit sha.");
        }

        return null;
    }
}
