using Thalos.Git.Workspaces;

namespace Thalos.Sandbox.Host;

/// <summary>
/// Refuses to run git over a repository whose own config was tampered with. Code an agent controls (MSBuild targets,
/// tests) runs as the same user as the host and can write <c>/work/repo/.git/config</c>: a <c>filter.*.clean</c> with a
/// <c>.gitattributes</c> entry, an <c>include.path</c>, a <c>diff.external</c> or a <c>core.fsmonitor</c> would make the
/// host's own <c>git add</c> or <c>git diff</c> run a command of the agent's choosing, or read config from elsewhere.
/// </summary>
/// <remarks>
/// Before every git command that reads the worktree, the repository's local config is listed (<c>git config --local
/// --list --name-only</c>, which does not follow includes) and any key outside a strict allow-list fails the command
/// closed. The list is what <c>git clone</c> and <c>git checkout</c> themselves write, read from real clones on Linux
/// and Windows: the <c>core</c> format keys, <c>remote.origin.url</c> and <c>.fetch</c>, and <c>branch.*.remote</c> and
/// <c>.merge</c>. GitCli also passes <c>core.fsmonitor=false</c> and an empty <c>core.hooksPath</c> on every call.
/// A write between the check and the command it guards is a residual same-user race; the publish side treats the patch
/// as adversarial regardless.
/// </remarks>
internal static class RepoConfigGuard
{
    private static readonly string[] Allowed =
    [
        "core.repositoryformatversion", "core.filemode", "core.bare", "core.logallrefupdates", "core.symlinks", "core.ignorecase",
        "remote.origin.url", "remote.origin.fetch",
    ];

    /// <summary>Null when the repository's local config holds only allowed keys; otherwise why git must not run.</summary>
    /// <param name="git">Runs git in isolation.</param>
    /// <param name="repo">The repository.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<string?> CheckAsync(GitCli git, string repo, CancellationToken ct)
    {
        var listed = await git.RunAsync(repo, ["config", "--local", "--list", "--name-only"], null, null, ct).ConfigureAwait(false);
        if (!listed.Succeeded)
        {
            return "the repository's git config could not be read";
        }

        foreach (var line in listed.StdOut.Split('\n'))
        {
            var name = line.Trim();
            if (name.Length > 0 && !IsAllowed(name))
            {
                return $"the repository's git config holds '{name}', which the sandbox does not allow";
            }
        }

        return null;
    }

    internal static bool IsAllowed(string name) =>
        Array.Exists(Allowed, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase))
        || (name.StartsWith("branch.", StringComparison.OrdinalIgnoreCase)
            && (name.EndsWith(".remote", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".merge", StringComparison.OrdinalIgnoreCase)));
}
