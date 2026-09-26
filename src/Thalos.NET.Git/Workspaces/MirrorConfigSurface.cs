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
/// <para>
/// <b>Comparison matches git's own case rules, not a blanket case-insensitive one.</b> A git config key has the
/// shape <c>section.subsection.name</c> (or <c>section.name</c> with no subsection): git itself always folds
/// <c>section</c> and <c>name</c> to lower case in its own <c>--list</c> output, regardless of how they were
/// written, but leaves <c>subsection</c> exactly as written — <c>remote.origin.url</c> and
/// <c>remote.ORIGIN.url</c> name two different remotes to git, the subsection being an arbitrary, case-sensitive
/// string. Since git has already done the section/name folding by the time <see cref="FindDisallowedKeyAsync"/>
/// reads <c>--list</c>'s output, comparing that output <em>ordinally</em> against this allow-list's own
/// already-lower-case entries reproduces git's exact rule for free: a same-cased key always matches, and
/// <c>remote.ORIGIN.url</c> — a config key this provider never wrote and did not intend — is refused as a key
/// outside the allow-list, exactly as it should be. An earlier, case-insensitive comparison here accepted it.
/// </para>
/// <para>
/// <b>Values are checked, not only keys.</b> Every key the provider itself writes must hold exactly the value the
/// provider wrote: <c>core.bare</c> must be <c>true</c>, <c>core.symlinks</c> must be <c>false</c>,
/// <c>remote.origin.url</c> must equal <see cref="FindDisallowedKeyAsync"/>'s own <c>remote</c> parameter when one
/// is given, and <c>remote.origin.fetch</c> must appear exactly once, equal to <see cref="ExpectedFetchRefspec"/> —
/// a value changed after create (or a second, additional value for a multi-valued key) is refused exactly as a new
/// key would be. Git's own repository-format keys (<c>core.repositoryformatversion</c>, <c>core.filemode</c>,
/// <c>core.ignorecase</c>) are checked for presence only, not value: they are git's own bookkeeping, not a lever an
/// attacker can pull toward code execution or credential exfiltration the way <c>core.symlinks</c> or
/// <c>remote.origin.url</c> are, and their value varies by filesystem (<c>core.filemode</c>,
/// <c>core.ignorecase</c>) in ways this type has no reason to pin down further.
/// </para>
/// <para>
/// <b><c>remote</c> is optional, and a caller's own choice of when to give it matters.</b>
/// <see cref="GitWorktreeWorkspaceProvider"/> reuses one mirror across every run against the same repository, and
/// <c>remote.origin.url</c> is deliberately overwritten before every fetch to whichever run's create is running —
/// see <see cref="GitWorktreeWorkspaceProvider"/>'s own remarks. A caller that checks the value <em>before</em>
/// that overwrite (validating an existing mirror it is about to reuse and re-target) must pass
/// <see langword="null"/>, or a create that legitimately changes a repository's remote between two runs would
/// refuse itself: the mirror still holds the previous run's URL at that point, not this run's own. A caller that
/// checks <em>after</em> its own write — <see cref="GitWorktreeWorkspaceProvider"/> itself, once more, right before
/// declaring a create complete, and <c>GitCliRunWorkspaceGit</c>'s <c>CommitAsync</c>/<c>PushAsync</c>, which have
/// no write of their own to wait on — passes its own trusted value and gets the full check.
/// </para>
/// </remarks>
internal static class MirrorConfigSurface
{
    /// <summary>The fetch refspec <see cref="GitWorktreeWorkspaceProvider"/> itself writes onto every mirror it clones.</summary>
    internal const string ExpectedFetchRefspec = "+refs/heads/*:refs/remotes/origin/*";

    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
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
    /// when every key is on <see cref="AllowedKeys"/> and the keys the provider writes hold exactly the values it
    /// wrote, or a detail message naming the first violation. <paramref name="root"/> may be the mirror itself or
    /// one of its worktrees — a worktree's own <c>--local</c> scope resolves to its mirror's shared config, so this
    /// reads identically either way.
    /// </summary>
    /// <param name="git">Runs the git call.</param>
    /// <param name="root">The mirror, or one of its worktrees.</param>
    /// <param name="remote">
    /// The remote <c>remote.origin.url</c> is expected to hold exactly. <see langword="null"/> skips that one
    /// value check — a supported configuration for a caller validating a mirror before overwriting
    /// <c>remote.origin.url</c> itself; see the class remarks.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<string?> FindDisallowedKeyAsync(GitCli git, string root, string? remote, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(git);

        // -z: NUL-terminated key/value pairs (key, then LF, then value, then NUL), so a value containing a newline
        // can never be mistaken for a second entry.
        var listed = await git.RunAsync(root, ["config", "--list", "--local", "-z"], null, null, ct).ConfigureAwait(false);
        if (!listed.Succeeded)
        {
            return listed.TimedOut
                ? "git config --list --local timed out."
                : $"could not list git config: {GitWorktreeWorkspaceProvider.ExtractErrorDetail(listed.StdErr)}";
        }

        var fetchCount = 0;
        foreach (var entry in listed.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var newline = entry.IndexOf('\n');
            if (newline < 0)
            {
                // A stray trailing newline after the last NUL-terminated record splits into its own, key-less
                // entry — a boundary artifact of git's own -z framing, never a real config key.
                continue;
            }

            var key = entry[..newline];
            var value = entry[(newline + 1)..];
            if (key.Length == 0)
            {
                continue;
            }

            if (!AllowedKeys.Contains(key))
            {
                return $"git config key '{key}' is outside the allowed surface.";
            }

            var violation = ValidateValue(key, value, remote, ref fetchCount);
            if (violation is not null)
            {
                return violation;
            }
        }

        return null;
    }

    /// <summary>
    /// Checks one already-allow-listed key's value against what the provider is known to write. Only the four keys
    /// named in the class remarks are checked; every other allow-listed key (git's own repository-format keys) is
    /// accepted with any value.
    /// </summary>
    private static string? ValidateValue(string key, string value, string? remote, ref int fetchCount)
    {
        switch (key)
        {
            case "core.bare" when !string.Equals(value, "true", StringComparison.Ordinal):
                return $"git config key 'core.bare' has an unexpected value '{value}'.";

            case "core.symlinks" when !string.Equals(value, "false", StringComparison.Ordinal):
                return $"git config key 'core.symlinks' has an unexpected value '{value}'.";

            case "remote.origin.url" when remote is not null && !string.Equals(value, remote, StringComparison.Ordinal):
                return "git config key 'remote.origin.url' does not match the configured remote.";

            case "remote.origin.fetch":
                fetchCount++;
                if (fetchCount > 1)
                {
                    return "git config key 'remote.origin.fetch' is set more than once.";
                }

                if (!string.Equals(value, ExpectedFetchRefspec, StringComparison.Ordinal))
                {
                    return $"git config key 'remote.origin.fetch' has an unexpected value '{value}'.";
                }

                break;
        }

        return null;
    }
}
