using ZeroAlloc.Authorization;

namespace Thalos.Workspaces;

/// <summary>The <see cref="ISecurityContext"/> claim that ties a caller to a run's workspace.</summary>
public static class RunWorkspaceClaims
{
    /// <summary>The ISecurityContext claim that ties a caller to a run's workspace. Set only by host code building the caller from the run row, never from a variable.</summary>
    public const string RunId = "thalos.run_id";

    /// <summary>The run id carried by <paramref name="caller"/>'s <see cref="RunId"/> claim, or <see langword="null"/> when absent or not a valid <see cref="Guid"/>.</summary>
    /// <param name="caller">The caller whose claims are read.</param>
    public static Guid? RunIdOf(ISecurityContext caller) =>
        caller.Claims.TryGetValue(RunId, out var raw) && Guid.TryParse(raw, out var id) ? id : null;
}
