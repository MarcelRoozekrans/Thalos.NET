using Thalos.Git.Workspaces;

namespace Thalos.Sandbox.Host;

/// <summary>
/// Refuses to run git while any config git would read was tampered with. Code an agent controls (MSBuild targets, tests)
/// runs as the same user as the host and can write <c>/work/repo/.git/config</c> and GitCli's own global config file
/// under the work volume: a <c>filter.*.clean</c> with a <c>.gitattributes</c> entry, an <c>include.path</c>, a
/// <c>diff.external</c> or a <c>core.fsmonitor</c> there would make the host's own <c>git add</c> or <c>git diff</c> run a
/// command of the agent's choosing.
/// </summary>
/// <remarks>
/// <para>
/// Before every git command that reads the worktree, the guard lists every config that command would read, with the same
/// environment and <c>-c</c> flags (<c>git config --list --show-scope --name-only --no-includes</c> through the same
/// <see cref="GitCli"/>), and fails closed on:
/// </para>
/// <list type="bullet">
/// <item>any <c>global</c>, <c>system</c> or <c>worktree</c> entry, or any other scope: the sandbox's git reads none;</item>
/// <item>a <c>command</c> entry other than the ones GitCli and <see cref="CommandConfig"/> pass;</item>
/// <item>a <c>local</c> key outside the allow-list, which is what <c>git clone</c> and <c>git checkout</c> themselves write,
/// read from real clones on Linux and Windows.</item>
/// </list>
/// <para>
/// Hooks: every sandbox git call passes <c>core.hooksPath=/dev/null</c> after GitCli's own, so no hook directory exists
/// for agent code to fill; <c>add</c>, <c>diff</c> and <c>config</c> run no hooks anyway, and <c>checkout</c> runs at
/// import, before any agent code has run. A write between the check and the command it guards is a residual same-user
/// race; the publish side treats the patch as adversarial regardless.
/// </para>
/// </remarks>
internal static class RepoConfigGuard
{
    /// <summary>The <c>-c</c> flags every sandbox git call passes on top of GitCli's own.</summary>
    public static readonly string[] CommandConfig = ["core.symlinks=false", "core.hooksPath=/dev/null"];

    private static readonly string[] AllowedLocal =
    [
        "core.repositoryformatversion", "core.filemode", "core.bare", "core.logallrefupdates", "core.symlinks", "core.ignorecase",
        "remote.origin.url", "remote.origin.fetch",
    ];

    /// <summary>The keys GitCli itself passes with <c>-c</c> on every call, plus <see cref="CommandConfig"/>'s.</summary>
    private static readonly string[] AllowedCommand =
    [
        "core.hookspath", "core.fsmonitor", "core.symlinks", "http.followredirects",
        "protocol.allow", "protocol.https.allow", "protocol.file.allow", "protocol.http.allow", "protocol.ext.allow", "protocol.git.allow", "protocol.ssh.allow",
    ];

    /// <summary>Null when every config git would read is acceptable; otherwise why git must not run.</summary>
    /// <param name="git">Runs git in isolation, as the guarded command will.</param>
    /// <param name="repo">The repository.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<string?> CheckAsync(GitCli git, string repo, CancellationToken ct)
    {
        var listed = await git.RunAsync(repo, ["config", "--list", "--show-scope", "--name-only", "--no-includes"], CommandConfig, null, ct).ConfigureAwait(false);
        if (!listed.Succeeded)
        {
            return "the git config could not be read";
        }

        foreach (var line in listed.StdOut.Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            var scope = tab < 0 ? "" : line[..tab];
            var name = (tab < 0 ? line : line[(tab + 1)..]).Trim();
            if (!IsAllowed(scope, name))
            {
                return $"the git config holds {scope} key '{name}', which the sandbox does not allow";
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="name"/> may appear in the <paramref name="scope"/> config.</summary>
    internal static bool IsAllowed(string scope, string name) => scope switch
    {
        "local" => Array.Exists(AllowedLocal, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase))
            || (name.StartsWith("branch.", StringComparison.OrdinalIgnoreCase)
                && (name.EndsWith(".remote", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".merge", StringComparison.OrdinalIgnoreCase))),
        "command" => Array.Exists(AllowedCommand, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };
}
