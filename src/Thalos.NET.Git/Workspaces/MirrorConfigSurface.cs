namespace Thalos.Git.Workspaces;

/// <summary>
/// The mirror's own git config is an attack surface a command-line <c>-c</c> pin cannot close: <c>url.&lt;x&gt;
/// .pushInsteadOf</c> can silently redirect a push — Authorization header included — to an untrusted host;
/// <c>filter.&lt;x&gt;.clean</c>/<c>.smudge</c> define arbitrary commands that run on <c>add</c>, <c>reset</c> and
/// <c>commit</c>; <c>core.hooksPath</c>, <c>core.fsmonitor</c> and any <c>protocol.*</c> override reopen exactly
/// what <see cref="GitCli"/>'s own command-line isolation closes (fix round 2, ruling: the provider owns the
/// mirror's config).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately an allow-list, not a deny-list of the keys reviewers happened to find: a deny-list drifts as git
/// grows new config surface, and each miss is a silent reopening. Only the keys
/// <see cref="GitWorktreeWorkspaceProvider"/> itself ever writes, plus git's own repository-format keys
/// <c>clone --bare</c> writes, are permitted. The bare-clone keys were enumerated empirically, not guessed, from a
/// real <c>git clone --bare</c> run on both Windows (git 2.54) and Linux (git 2.43): both wrote
/// <c>core.repositoryformatversion</c>, <c>core.bare</c> and <c>core.filemode</c>; Windows alone also wrote
/// <c>core.ignorecase</c> (its case-insensitive filesystem) and <c>core.symlinks</c> (which
/// <see cref="GitWorktreeWorkspaceProvider"/> writes explicitly afterward regardless, on every platform). Neither
/// run wrote <c>core.precomposeunicode</c> or <c>core.logallrefupdates</c>, so neither is allow-listed; a future
/// git version, or a platform this was not run on, that starts writing a new key refuses every mirror closed until
/// this list is updated — an accepted cost, since failing closed on an unrecognised key is exactly the point.
/// </para>
/// <para>
/// A key this allow-list has never heard of is refused regardless of what it is — including
/// <c>extensions.worktreeConfig</c>, the repository extension that would let each worktree carry its own, separate
/// <c>config.worktree</c> file layered on top of the shared mirror config it is not itself allow-listed, so a
/// mirror can never turn it on in the first place, and no worktree this provider creates can ever have a
/// config.worktree file take effect.
/// </para>
/// </remarks>
internal static class MirrorConfigSurface
{
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        // Written by GitWorktreeWorkspaceProvider itself: CloneMirrorAsync and PrepareMirrorAsync.
        "remote.origin.url",
        "remote.origin.fetch",
        "core.symlinks",

        // Written by "git clone --bare" itself — see the class remarks for how this was enumerated.
        "core.repositoryformatversion",
        "core.filemode",
        "core.bare",
        "core.ignorecase",
    };

    /// <summary>
    /// Reads <paramref name="root"/>'s local git config with <paramref name="git"/> and returns <see langword="null"/>
    /// when every key is on <see cref="AllowedKeys"/>, or a detail message naming the first one that is not.
    /// <paramref name="root"/> may be the mirror itself or one of its worktrees — a worktree's own <c>--local</c>
    /// scope resolves to its mirror's shared config, so this reads identically either way.
    /// </summary>
    public static async Task<string?> FindDisallowedKeyAsync(GitCli git, string root, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(git);

        // -z: NUL-terminated key/value pairs (key, then LF, then value, then NUL), so a value containing a newline
        // can never be mistaken for a second entry. Only the key half of each pair is inspected.
        var listed = await git.RunAsync(root, ["config", "--list", "--local", "-z"], null, null, ct).ConfigureAwait(false);
        if (!listed.Succeeded)
        {
            return listed.TimedOut
                ? "git config --list --local timed out."
                : $"could not list git config: {GitWorktreeWorkspaceProvider.ExtractErrorDetail(listed.StdErr)}";
        }

        foreach (var entry in listed.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var newline = entry.IndexOf('\n');
            var key = newline < 0 ? entry : entry[..newline];
            if (key.Length == 0)
            {
                // A stray trailing newline after the last NUL-terminated record splits into its own, key-less
                // entry — a boundary artifact of git's own -z framing, never a real config key.
                continue;
            }

            if (!AllowedKeys.Contains(key))
            {
                return $"git config key '{key}' is outside the allowed surface.";
            }
        }

        return null;
    }
}
