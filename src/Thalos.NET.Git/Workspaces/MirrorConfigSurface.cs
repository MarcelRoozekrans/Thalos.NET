namespace Thalos.Git.Workspaces;

/// <summary>
/// The git configuration a run's mirror and worktrees are allowed to carry. The mirror's own config, and a worktree's
/// own <c>config.worktree</c>, are an attack surface a command-line <c>-c</c> pin cannot close:
/// <c>url.&lt;x&gt;.pushInsteadOf</c> can redirect a push, Authorization header included, to an untrusted host;
/// <c>filter.&lt;x&gt;.clean</c> and <c>.smudge</c> define arbitrary commands that run on <c>add</c>, <c>reset</c>
/// and <c>commit</c>; <c>core.hooksPath</c>, <c>core.fsmonitor</c> and any <c>protocol.*</c> override reopen what
/// <see cref="GitCli"/>'s own isolation closes. The provider owns this config, so anything it did not write is
/// refused.
/// </summary>
/// <remarks>
/// <para>
/// <b>Git is asked, never parsed by hand.</b> <see cref="FindViolationAsync"/> runs
/// <c>git config --list --name-only --show-scope -z</c> in the directory under check, through <see cref="GitCli"/>, so
/// it sees exactly the keys git itself will use there, with the scope each came from: the repository's shared config,
/// a worktree's <c>config.worktree</c> when <c>extensions.worktreeConfig</c> is on, anything an include pulls in, and
/// <see cref="GitCli"/>'s own <c>-c</c> pins. Values are read back with git's own typing:
/// <c>git config --type=bool --get</c> for <c>core.bare</c> and <c>core.symlinks</c>, and
/// <c>git config -z --get-all</c> for <c>remote.origin.fetch</c> and <c>remote.origin.url</c>. A valueless key, such
/// as <c>[core] symlinks</c> with no <c>=</c>, therefore reads as <c>true</c> exactly as git reads it. An earlier
/// revision parsed <c>git config --list --local -z</c> itself, and fell to both a valueless key, which git prints
/// with no newline, and <c>config.worktree</c>, which <c>--local</c> never reads (fix round 4 ruling).
/// </para>
/// <para>
/// <b>Scopes.</b> A key in <c>command</c> scope is one of <see cref="GitCli"/>'s own <c>-c</c> isolation pins, and
/// is ignored. A key in <c>local</c> scope must be on <see cref="AllowedLocalKeys"/>. A key in any other scope is
/// refused whatever it is: <c>worktree</c>, because this provider never writes a <c>config.worktree</c>; and
/// <c>global</c> or <c>system</c>, which <see cref="GitCli"/>'s isolation keeps empty, so a key there means that
/// isolation failed.
/// </para>
/// <para>
/// <b>The allow-list.</b> Only the keys <see cref="GitWorktreeWorkspaceProvider"/> itself writes, plus git's own
/// repository-format keys that <c>clone --bare</c> writes, are permitted. The bare-clone keys were enumerated from a
/// real <c>git clone --bare</c> on Windows (git 2.54) and Linux (git 2.43): both wrote
/// <c>core.repositoryformatversion</c>, <c>core.bare</c> and <c>core.filemode</c>; Windows also wrote
/// <c>core.ignorecase</c> and <c>core.symlinks</c>. A git version that starts writing a new key refuses every mirror
/// until this list is updated, which fails closed. <c>extensions.worktreeConfig</c> is not on the list, so a mirror
/// can never turn per-worktree config on. Git folds a key's section and name to lower case in its own output and
/// keeps a subsection's case, so comparing ordinally against these lower-case entries reproduces git's own rule:
/// <c>remote.ORIGIN.url</c> is a different key to git, and is refused.
/// </para>
/// <para>
/// <b>Values.</b> <c>core.bare</c> must read <c>true</c> and <c>core.symlinks</c> <c>false</c>;
/// <c>remote.origin.fetch</c> must hold exactly one value, <see cref="ExpectedFetchRefspec"/>; and
/// <c>remote.origin.url</c> must hold exactly one value, the caller's <c>remote</c>, when one is given. Git's own
/// format keys are checked for presence only, since their values vary by filesystem and none of them runs code.
/// </para>
/// <para>
/// <b><c>remote</c> is optional.</b> <see cref="GitWorktreeWorkspaceProvider"/> reuses one mirror across every run
/// against the same repository and overwrites <c>remote.origin.url</c> before every fetch. A caller checking a
/// mirror before that overwrite passes <see langword="null"/>, or a create that legitimately changes a repository's
/// remote would refuse itself. A caller checking after its own write, the provider right before it declares a create
/// complete, passes its own value; so do <see cref="GitCliRunWorkspaceGit"/>'s commit and push, which write nothing.
/// </para>
/// </remarks>
internal static class MirrorConfigSurface
{
    /// <summary>The fetch refspec <see cref="GitWorktreeWorkspaceProvider"/> itself writes onto every mirror it clones.</summary>
    internal const string ExpectedFetchRefspec = "+refs/heads/*:refs/remotes/origin/*";

    private static readonly HashSet<string> AllowedLocalKeys = new(StringComparer.Ordinal)
    {
        // Written by GitWorktreeWorkspaceProvider itself: CloneMirrorAsync and PrepareMirrorAsync.
        "remote.origin.url",
        "remote.origin.fetch",
        "core.symlinks",

        // Written by "git clone --bare" itself; see the class remarks for how this was enumerated.
        "core.repositoryformatversion",
        "core.filemode",
        "core.bare",
        "core.ignorecase",
    };

    /// <summary>
    /// Asks git for every config key it will use in <paramref name="directory"/>, and for the values of the keys the
    /// provider writes, and returns <see langword="null"/> when all of them are allowed, or a detail message naming
    /// the first violation.
    /// </summary>
    /// <param name="git">Runs every git call, under its own isolation.</param>
    /// <param name="directory">The mirror, or one of its worktrees. A worktree also sees its own <c>config.worktree</c>.</param>
    /// <param name="remote">
    /// The single value <c>remote.origin.url</c> must hold. <see langword="null"/> skips that one value check, for a
    /// caller about to overwrite <c>remote.origin.url</c> itself; see the class remarks.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<string?> FindViolationAsync(GitCli git, string directory, string? remote, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(git);

        var listed = await git.RunAsync(directory, ["config", "--list", "--name-only", "--show-scope", "-z"], null, null, ct).ConfigureAwait(false);
        if (!listed.Succeeded)
        {
            return Failed("git config --list", listed);
        }

        var disallowed = FindDisallowedKey(listed.StdOut);
        if (disallowed is not null)
        {
            return disallowed;
        }

        return await CheckBoolAsync(git, directory, "core.bare", "true", ct).ConfigureAwait(false)
            ?? await CheckBoolAsync(git, directory, "core.symlinks", "false", ct).ConfigureAwait(false)
            ?? await CheckSingleValueAsync(git, directory, "remote.origin.fetch", ExpectedFetchRefspec, ct).ConfigureAwait(false)
            ?? (remote is null ? null : await CheckSingleValueAsync(git, directory, "remote.origin.url", remote, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Checks the output of <c>git config --list --name-only --show-scope -z</c>: a scope and a key per entry, each
    /// terminated by NUL. Returns <see langword="null"/> when every key is in <c>command</c> scope, or in
    /// <c>local</c> scope and on <see cref="AllowedLocalKeys"/>; otherwise a detail message naming the first key that
    /// is not. Output that does not pair up is refused.
    /// </summary>
    /// <param name="listing">The command's standard output, as <see cref="GitCli"/> captured it.</param>
    internal static string? FindDisallowedKey(string listing)
    {
        ArgumentNullException.ThrowIfNull(listing);

        // GitCli captures standard output line by line and ends each line with '\n'. The listing has no newline of
        // its own, since a config key can never hold one, so this strips exactly that one terminator.
        var text = listing.EndsWith('\n') ? listing[..^1] : listing;
        if (text.Length == 0)
        {
            return null;
        }

        if (!text.EndsWith('\0'))
        {
            return "git config --list printed output that does not end in NUL.";
        }

        var tokens = text[..^1].Split('\0');
        if (tokens.Length % 2 != 0)
        {
            return "git config --list printed a scope without a key.";
        }

        for (var i = 0; i < tokens.Length; i += 2)
        {
            var (scope, key) = (tokens[i], tokens[i + 1]);
            switch (scope)
            {
                case "command":
                    continue;
                case "local" when AllowedLocalKeys.Contains(key):
                    continue;
                case "local":
                    return $"git config key '{key}' is outside the allowed surface.";
                default:
                    return $"git config key '{key}' is set in {scope} scope; only the repository's own config is allowed.";
            }
        }

        return null;
    }

    /// <summary>
    /// Reads <paramref name="key"/> with <c>git config --type=bool --get</c>, git's own typing, under which a
    /// valueless key is <c>true</c> and the last value wins, and requires <paramref name="expected"/>. A missing key
    /// or a value git cannot read as a boolean is refused.
    /// </summary>
    private static async Task<string?> CheckBoolAsync(GitCli git, string directory, string key, string expected, CancellationToken ct)
    {
        var read = await git.RunAsync(directory, ["config", "--type=bool", "--get", key], null, null, ct).ConfigureAwait(false);
        if (!read.Succeeded)
        {
            return Failed($"git config --type=bool --get {key}", read);
        }

        var value = read.StdOut.Trim();
        return string.Equals(value, expected, StringComparison.Ordinal)
            ? null
            : $"git config key '{key}' reads as '{value}', not '{expected}'.";
    }

    /// <summary>
    /// Reads every value of <paramref name="key"/> with <c>git config -z --get-all</c> and requires exactly one,
    /// equal to <paramref name="expected"/>. With <c>-z</c> git ends each value with NUL, so the whole output must
    /// be exactly <paramref name="expected"/> and one NUL; a second value, even an identical one, is refused.
    /// </summary>
    private static async Task<string?> CheckSingleValueAsync(GitCli git, string directory, string key, string expected, CancellationToken ct)
    {
        var read = await git.RunAsync(directory, ["config", "-z", "--get-all", key], null, null, ct).ConfigureAwait(false);
        if (!read.Succeeded)
        {
            return Failed($"git config --get-all {key}", read);
        }

        var output = read.StdOut.EndsWith('\n') ? read.StdOut[..^1] : read.StdOut;
        return string.Equals(output, expected + "\0", StringComparison.Ordinal)
            ? null
            : $"git config key '{key}' does not hold exactly one value equal to the one the provider wrote.";
    }

    private static string Failed(string command, GitCliResult result) =>
        result.TimedOut
            ? $"{command} timed out."
            : $"{command} failed with exit code {result.ExitCode}: {GitWorktreeWorkspaceProvider.ExtractErrorDetail(result.StdErr)}";
}
