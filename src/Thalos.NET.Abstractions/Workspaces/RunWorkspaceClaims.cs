using System.Linq;
using ZeroAlloc.Authorization;

namespace Thalos.Workspaces;

/// <summary>The <see cref="ISecurityContext"/> claim that ties a caller to a run's workspace.</summary>
/// <remarks>
/// <b>Set these claims only from the run row, never pass them through.</b> The <see cref="RunId"/> claim alone
/// decides which run's workspace the <c>workspace__*</c> tools act on and which run's own MCP servers a run-scoped
/// call is routed to, and <see cref="WriteExtensions"/> decides what that caller may write there. A host builds the
/// caller for a run's turn from that run's row and from reviewed config only. It must never copy <c>thalos.*</c>
/// claims from an inbound identity, such as the claims of a JWT or another external token, and never from a run
/// variable: whoever can set <see cref="RunId"/> can reach any run's workspace and servers. A host that maps an
/// external identity into an <see cref="ISecurityContext"/> drops every inbound claim whose name starts with
/// <c>thalos.</c>.
/// </remarks>
public static class RunWorkspaceClaims
{
    /// <summary>The ISecurityContext claim that ties a caller to a run's workspace. Set only by host code building the caller from the run row, never from a variable.</summary>
    public const string RunId = "thalos.run_id";

    /// <summary>
    /// The extensions the caller's write grant allows, separated by <c>';'</c>, e.g. <c>".cs;.md"</c>. Set only by
    /// host code from reviewed config. Absent = the options' ceiling alone applies — see
    /// <c>RunWorkspaceToolOptions.AllowedWriteExtensions</c> and ruling R29.
    /// </summary>
    public const string WriteExtensions = "thalos.workspace.write_extensions";

    /// <summary>The run id carried by <paramref name="caller"/>'s <see cref="RunId"/> claim, or <see langword="null"/> when absent or not a valid <see cref="Guid"/>.</summary>
    /// <param name="caller">The caller whose claims are read.</param>
    public static Guid? RunIdOf(ISecurityContext caller) =>
        caller.Claims.TryGetValue(RunId, out var raw) && Guid.TryParse(raw, out var id) ? id : null;

    /// <summary>
    /// The caller's <see cref="WriteExtensions"/> grant, split on <c>';'</c>, trimmed and compared
    /// case-insensitively, or <see langword="null"/> only when the claim is <em>absent</em> — meaning the caller
    /// carries no grant at all, so the host-wide ceiling alone applies. A <em>present</em> claim always parses to a
    /// set, even an empty one: a blank value (<c>""</c> or whitespace) is a grant of zero extensions, not "no
    /// grant", and narrows the ceiling down to nothing rather than leaving it unnarrowed.
    /// </summary>
    /// <param name="caller">The caller whose claims are read.</param>
    public static IReadOnlySet<string>? WriteExtensionsOf(ISecurityContext caller) =>
        caller.Claims.TryGetValue(WriteExtensions, out var raw)
            ? raw.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
}
