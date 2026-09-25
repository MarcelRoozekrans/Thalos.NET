using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Thalos.Git.Workspaces;

/// <summary>The exit code, standard output and standard error of one git invocation.</summary>
/// <param name="ExitCode">The process exit code. Meaningless when <paramref name="TimedOut"/> is <see langword="true"/>.</param>
/// <param name="StdOut">Everything the process wrote to standard output.</param>
/// <param name="StdErr">Everything the process wrote to standard error.</param>
/// <param name="TimedOut">
/// <see langword="true"/> when <see cref="GitWorkspaceOptions.CommandTimeout"/> elapsed before the process exited
/// and it was killed. A timeout is a result, never an exception — see <see cref="GitCli"/>'s remarks.
/// </param>
internal readonly record struct GitCliResult(int ExitCode, string StdOut, string StdErr, bool TimedOut = false)
{
    /// <summary><see langword="true"/> when the process exited on its own with code 0.</summary>
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>
/// Runs the git executable as a real child <see cref="Process"/>, awaited with
/// <see cref="Process.WaitForExitAsync(CancellationToken)"/> — never LibGit2Sharp, and never <see cref="Task.Run{TResult}(Func{TResult})"/>
/// to fake async over a synchronous API (ruling R23). Every call is independent; nothing is cached or kept open
/// between calls, except the one-time git version check and the isolation directory the constructor empties.
/// </summary>
/// <remarks>
/// <para>
/// <b>Arguments.</b> <see cref="RunAsync"/> takes its <c>args</c> as a list appended verbatim to
/// <see cref="ProcessStartInfo.ArgumentList"/> — never a concatenated command string — so nothing a caller passes
/// (a branch name, a remote URL, a run id) is ever interpreted by a shell. Each <c>extraConfig</c> entry becomes a
/// separate <c>-c &lt;entry&gt;</c> pair placed before <c>args</c>, for a non-secret override such as disabling
/// symlinks on a checkout. A secret — the one case today is a credential header — never goes through
/// <c>extraConfig</c>: see <c>secretConfig</c> below.
/// </para>
/// <para>
/// <b>Isolation from host git configuration.</b> A user or system gitconfig can carry a <c>core.hooksPath</c> that
/// runs an arbitrary script on checkout, or a <c>protocol.ext.allow=always</c> that turns an <c>ext::</c> "remote"
/// into an arbitrary shell command — both of which this type's caller does not expect and cannot see, since the
/// remote and branch names it passes come from configuration and run ids, not from a human sitting at this host's
/// git identity. Every call therefore sets <c>GIT_CONFIG_NOSYSTEM=1</c> and <c>GIT_CONFIG_GLOBAL</c> to an empty
/// file this type creates under <see cref="GitWorkspaceOptions.DataRoot"/>, so <em>no</em> system or user gitconfig
/// is read at all — not filtered, not overridden key by key, simply never opened. <c>GIT_CONFIG_GLOBAL</c> is a
/// git 2.32 feature, which is why the constructor's first real command checks the git version and every call fails
/// closed (a non-<see cref="GitCliResult.Succeeded"/> result, not an exception) until that check passes. On top of
/// removing the global and system layers entirely, every call also passes <c>-c core.hooksPath=&lt;an empty
/// provider-owned directory&gt;</c> (so even a hook a caller-supplied <c>extraConfig</c> or the repository's own
/// tracked config might name cannot exist to run) and <c>-c protocol.allow=never</c> together with every individual
/// <c>protocol.&lt;name&gt;.allow</c> git itself recognises, each pinned explicitly rather than left to the
/// <c>protocol.allow</c> default: <c>https</c> and <c>file</c> set to <c>always</c> (the only two transports this
/// provider actually uses), and <c>http</c>, <c>ext</c>, <c>git</c> and <c>ssh</c> each set to <c>never</c> (fix
/// round 2 ruling — pinning only <c>protocol.allow=never</c> left a per-protocol override in a repository's own
/// config, e.g. <c>protocol.http.allow=always</c>, able to widen it). Every call also passes
/// <c>-c http.followRedirects=false</c> (fix round 2 ruling, pre-existing): an origin's HTTP redirect otherwise
/// makes git resend a request — headers included — to whatever host the redirect names, which this provider never
/// controls or intends to trust. Removing the global and system config layers is the root fix; the hooksPath,
/// protocol and redirect flags are stated explicitly on the command line as well because they are cheap to state
/// and give defense in depth against a future caller who reintroduces a config source this type does not control.
/// </para>
/// <para>
/// <b>Secrets never reach argv or disk.</b> <c>secretConfig</c> entries — one <c>(key, value)</c> pair for a
/// credential header — are passed through <c>GIT_CONFIG_COUNT</c>, <c>GIT_CONFIG_KEY_&lt;n&gt;</c> and
/// <c>GIT_CONFIG_VALUE_&lt;n&gt;</c> environment variables instead of <c>-c</c>. A command-line argument is visible
/// to any other process on the same host that can read <c>/proc/&lt;pid&gt;/cmdline</c> on Linux, or an equivalent
/// on Windows; the environment block of a child process this one starts is not.
/// </para>
/// <para>
/// <b>Other environment isolation.</b> The child starts from the host process's own environment — so <c>PATH</c>,
/// <c>TEMP</c>, and on Windows <c>SYSTEMROOT</c>, everything the git executable itself needs to run, are inherited
/// — and then every inherited <c>GIT_*</c> variable is stripped: any one of them (<c>GIT_DIR</c>,
/// <c>GIT_WORK_TREE</c>, <c>GIT_SSH_COMMAND</c>, a stray <c>GIT_CONFIG_GLOBAL</c> pointing somewhere else, ...)
/// could silently redirect an operation the caller believes is confined to <see cref="RunAsync"/>'s own
/// <c>workingDirectory</c>. <c>SSH_ASKPASS</c> is stripped too, since it names an external program git will launch
/// the same way a terminal prompt would ask. This type's own <c>GIT_*</c> variables (<c>GIT_TERMINAL_PROMPT</c>,
/// <c>GIT_CONFIG_NOSYSTEM</c>, <c>GIT_CONFIG_GLOBAL</c>, and <c>GIT_CONFIG_COUNT</c>/<c>KEY_n</c>/<c>VALUE_n</c>
/// when <c>secretConfig</c> is given) are set after the strip, so they are never accidentally removed by it.
/// <c>GIT_TERMINAL_PROMPT=0</c> means a missing credential fails the command instead of blocking indefinitely on a
/// prompt that never comes; <c>GCM_INTERACTIVE=Never</c> is set because Git Credential Manager — the default
/// credential helper on Git for Windows — can still raise its own GUI prompt even with terminal prompting off; and
/// <c>LC_ALL=C</c> / <c>LANGUAGE=C</c> mean git's own messages are in English and stable across hosts, which matters
/// because <see cref="GitWorktreeWorkspaceProvider"/> parses <see cref="GitCliResult.StdErr"/> for an
/// <see cref="AgentError.Detail"/> and scrubs a credential value out of it first — a localized message would defeat
/// both.
/// </para>
/// <para>
/// <b><c>HOME</c> and <c>XDG_CONFIG_HOME</c> are also isolated.</b> <c>GIT_CONFIG_NOSYSTEM</c>/<c>GIT_CONFIG_GLOBAL</c>
/// close every gitconfig-based read, but <c>~/.netrc</c> is a separate mechanism: git's own HTTP transport reads it
/// directly for credentials, keyed off <c>HOME</c>, with no gitconfig involved at all — so closing the config paths
/// alone leaves it open. Every call sets both <c>HOME</c> and <c>XDG_CONFIG_HOME</c> to the same empty,
/// provider-owned directory (recreated empty in the constructor — see below), so <c>~/.netrc</c> and any other
/// home-keyed read (SSH's own config, credential caches, ...) find nothing. <c>HTTPS_PROXY</c>, <c>SSL_CERT_FILE</c>
/// and <c>CURL_CA_BUNDLE</c> are deliberately <em>not</em> stripped or overridden — a host that needs a proxy or a
/// custom CA bundle for its git traffic configures it through the process environment as normal, and this type does
/// not get in the way of that, only of configuration sources tied to a human identity on this host.
/// </para>
/// <para>
/// <b>The isolation directories are emptied on every construction, and verified empty.</b> Git reads more than
/// gitconfig from <c>HOME</c> and <c>XDG_CONFIG_HOME</c>: <c>~/.netrc</c> for credentials, and
/// <c>$XDG_CONFIG_HOME/git/attributes</c> and <c>git/ignore</c>, which <c>GIT_CONFIG_GLOBAL</c> does not cover. So
/// the constructor empties the whole <c>.git-isolation</c> directory under <see cref="GitWorkspaceOptions.DataRoot"/>
/// recursively, whatever is nested in it and whether or not it is read-only: everything except the hooks directory,
/// the home directory and the global config file is deleted, the two directories are emptied, and the global config
/// is truncated. A link is deleted as a link, never followed, so a planted link cannot make the constructor delete
/// anything outside <c>.git-isolation</c>. <see cref="GitWorkspaceOptions.DataRoot"/> is canonicalised once with
/// <see cref="Path.GetFullPath(string)"/>, and the three kept entries are recognised by name, so any spelling of the
/// same directory — forward slashes, <c>..</c>, doubled separators — works. Only "already gone" is tolerated while
/// deleting; any other failure propagates. The constructor then verifies that the hooks and home directories are empty, the global config is an
/// empty file, and nothing else is there, and throws <see cref="InvalidOperationException"/> if anything remains:
/// git never runs over a planted file this type could not remove. Constructors sharing a
/// <see cref="GitWorkspaceOptions.DataRoot"/> — this provider is a DI singleton, and an API host and a CLI host may
/// share one — take turns, through a <see cref="CrossProcessFileLock"/> on <c>&lt;DataRoot&gt;/locks/isolation.lock</c>
/// held for the whole empty-and-verify, bounded by <see cref="GitWorkspaceOptions.CommandTimeout"/>. Without it, two
/// constructors deleting the same entries at once fail on Windows, where a file another thread is deleting reports
/// access denied rather than not found. Nothing but a constructor writes into these directories, so a git call
/// running in another instance while one is being emptied is unaffected.
/// </para>
/// <para>
/// <b>Timeout.</b> <see cref="GitWorkspaceOptions.CommandTimeout"/> bounds a single invocation; on expiry the whole
/// process tree is killed and <see cref="RunAsync"/> returns a result with <see cref="GitCliResult.TimedOut"/> set,
/// never a thrown exception — a caller that let a <see cref="TimeoutException"/> escape mid-<c>CreateAsync</c>
/// would skip its own cleanup and leave a half-cloned mirror behind for every later call to trip over. Cancelling
/// the caller's own token still kills the process the same way, but propagates as <see cref="OperationCanceledException"/>,
/// same as everywhere else in this codebase — see ruling for B5.
/// </para>
/// </remarks>
internal sealed partial class GitCli
{
    [GeneratedRegex(@"(?<major>\d+)\.(?<minor>\d+)(?:\.\d+)?", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VersionPattern();

    private readonly GitWorkspaceOptions _options;
    private readonly string _hooksDirectory;
    private readonly string _homeDirectory;
    private readonly string _globalConfigPath;
    private volatile bool _versionConfirmed;

    private const string HooksName = "hooks";
    private const string HomeName = "home";
    private const string GlobalConfigName = "global.config";

    public GitCli(GitWorkspaceOptions options)
    {
        _options = options;

        // Canonical once, here: a DataRoot spelled with forward slashes, "..", "." or doubled separators names the
        // same directory, and every path below is built from this one form. Entries are recognised by name, never by
        // comparing a built path with a normalised FullName, so no spelling of DataRoot can make a match fail.
        var dataRoot = Path.GetFullPath(options.DataRoot);
        var isolationDirectory = Path.Combine(dataRoot, ".git-isolation");
        _hooksDirectory = Path.Combine(isolationDirectory, HooksName);
        _homeDirectory = Path.Combine(isolationDirectory, HomeName);
        _globalConfigPath = Path.Combine(isolationDirectory, GlobalConfigName);

        // Emptied every time, never assumed already empty, then verified — see the class remarks. One constructor at a
        // time per DataRoot, across processes, so no two ever delete the same entries at once.
        using (CrossProcessFileLock.Acquire(Path.Combine(dataRoot, "locks", "isolation.lock"), options.CommandTimeout))
        {
            EnsureRealDirectory(isolationDirectory);
            EmptyIsolationDirectory(isolationDirectory);
            EnsureRealDirectory(_hooksDirectory);
            EnsureRealDirectory(_homeDirectory);
            ClearGlobalConfig(_globalConfigPath);
            VerifyIsolation(isolationDirectory);
        }
    }

    /// <summary>
    /// Creates <paramref name="path"/> as a real directory. A link planted in its place is removed first, as a link,
    /// so emptying the directory can never reach through it into somewhere else.
    /// </summary>
    private static void EnsureRealDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if (info.Exists && info.LinkTarget is not null)
        {
            DeleteEntry(info);
        }

        Directory.CreateDirectory(path);
    }

    /// <summary>
    /// Deletes everything in <paramref name="isolationDirectory"/> except the hooks directory, the home directory and
    /// the global config, and empties those two directories recursively.
    /// </summary>
    private static void EmptyIsolationDirectory(string isolationDirectory)
    {
        foreach (var entry in EnumerateEntries(isolationDirectory))
        {
            var isLink = entry.LinkTarget is not null;
            if (!isLink && entry is DirectoryInfo && IsKeptDirectory(entry))
            {
                EmptyDirectory(entry.FullName);
            }
            else if (!isLink && entry is FileInfo && IsName(entry, GlobalConfigName))
            {
                ClearReadOnly(entry);
            }
            else
            {
                DeleteEntry(entry);
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="entry"/>, a direct entry of the isolation directory, has exactly <paramref name="name"/>.
    /// Ordinal: on a case-insensitive file system a planted <c>HOOKS</c> is deleted and <c>hooks</c> recreated, which
    /// is safe.
    /// </summary>
    private static bool IsName(FileSystemInfo entry, string name) => string.Equals(entry.Name, name, StringComparison.Ordinal);

    private static bool IsKeptDirectory(FileSystemInfo entry) => IsName(entry, HooksName) || IsName(entry, HomeName);

    /// <summary>Deletes every entry inside <paramref name="directory"/>, recursively, leaving the directory itself.</summary>
    private static void EmptyDirectory(string directory)
    {
        foreach (var entry in EnumerateEntries(directory))
        {
            DeleteEntry(entry);
        }
    }

    /// <summary>
    /// Deletes one entry: a link as a link, never following it; a directory after emptying it; a file after clearing
    /// its read-only attribute. Tolerates only the entry, or its parent, being gone already.
    /// </summary>
    private static void DeleteEntry(FileSystemInfo entry)
    {
        try
        {
            if (entry.LinkTarget is not null)
            {
                if (entry is DirectoryInfo)
                {
                    Directory.Delete(entry.FullName);
                }
                else
                {
                    File.Delete(entry.FullName);
                }

                return;
            }

            ClearReadOnly(entry);
            if (entry is DirectoryInfo)
            {
                EmptyDirectory(entry.FullName);
                Directory.Delete(entry.FullName);
            }
            else
            {
                File.Delete(entry.FullName);
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // A concurrent constructor emptying the same directory deleted it first.
        }
    }

    /// <summary>Clears the read-only attribute of a non-link entry, so it, or for a directory its contents, can be deleted.</summary>
    private static void ClearReadOnly(FileSystemInfo entry)
    {
        var attributes = entry.Attributes;
        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            entry.Attributes = attributes & ~FileAttributes.ReadOnly;
        }
    }

    /// <summary>
    /// The entries of <paramref name="directory"/>, or none when a concurrent constructor removed it first. Listed
    /// eagerly, so deleting entries does not disturb the enumeration.
    /// </summary>
    private static FileSystemInfo[] EnumerateEntries(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).GetFileSystemInfos();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    /// <summary>
    /// Throws unless the isolation directory holds exactly an empty hooks directory, an empty home directory and an
    /// empty global config file — so git never runs over anything the emptying could not remove.
    /// </summary>
    private static void VerifyIsolation(string isolationDirectory)
    {
        foreach (var entry in new DirectoryInfo(isolationDirectory).GetFileSystemInfos())
        {
            if (!IsExpectedIsolationEntry(entry))
            {
                throw new InvalidOperationException(
                    $"The git isolation directory '{isolationDirectory}' could not be emptied: '{entry.FullName}' remains. Git will not run over it.");
            }
        }
    }

    private static bool IsExpectedIsolationEntry(FileSystemInfo entry)
    {
        if (entry.LinkTarget is not null)
        {
            return false;
        }

        return entry switch
        {
            DirectoryInfo directory => IsKeptDirectory(directory) && directory.GetFileSystemInfos().Length == 0,
            FileInfo file => IsName(file, GlobalConfigName) && file.Length == 0,
            _ => false,
        };
    }

    /// <summary>
    /// Truncates <paramref name="path"/> to empty, creating it first if absent. Opened with
    /// <see cref="FileShare.ReadWrite"/> rather than <see cref="File.WriteAllText(string, string)"/>'s exclusive
    /// default: two instances constructing at once on a shared <see cref="GitWorkspaceOptions.DataRoot"/> both
    /// truncate the same file, and since every writer truncates to the same empty content, a second writer opening
    /// while the first is still mid-call is harmless as long as it is not refused outright — which an exclusive
    /// open would do (observed under concurrent construction: <see cref="IOException"/>, "being used by another
    /// process").
    /// </summary>
    private static void ClearGlobalConfig(string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
    }

    /// <summary>Runs <c>git [-c &lt;extraConfig&gt;]* &lt;args&gt;</c> in <paramref name="workingDirectory"/>.</summary>
    /// <param name="workingDirectory">The directory git runs in — equivalent to a leading <c>git -C &lt;workingDirectory&gt;</c>.</param>
    /// <param name="args">The subcommand and its arguments, one <see cref="ProcessStartInfo.ArgumentList"/> entry each.</param>
    /// <param name="extraConfig">
    /// Zero or more non-secret <c>key=value</c> config overrides, each passed as its own <c>-c</c> flag on the
    /// command line only — never written to a config file. <see langword="null"/> or empty passes none.
    /// </param>
    /// <param name="secretConfig">
    /// Zero or more secret <c>(key, value)</c> config overrides — credentials — passed through
    /// <c>GIT_CONFIG_COUNT</c>/<c>GIT_CONFIG_KEY_n</c>/<c>GIT_CONFIG_VALUE_n</c> environment variables, never argv
    /// and never disk. <see langword="null"/> or empty passes none.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<GitCliResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        IReadOnlyList<string>? extraConfig,
        IReadOnlyList<(string Key, string Value)>? secretConfig,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(args);

        var versionProblem = await EnsureVersionAsync(ct).ConfigureAwait(false);
        if (versionProblem is not null)
        {
            return new GitCliResult(1, string.Empty, versionProblem);
        }

        return await ExecuteAsync(workingDirectory, args, extraConfig, secretConfig, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <c>git --version</c> and reports why it is unusable, or <see langword="null"/> when it is at least
    /// 2.32 — the version <c>GIT_CONFIG_GLOBAL</c> needs, which is this type's whole host-isolation mechanism (see
    /// the class remarks). Only a successful check is memoized (in <see cref="_versionConfirmed"/>): a failure —
    /// including a transient one, such as a timeout — is not cached, so a later call retries rather than staying
    /// permanently stuck on one bad reading for the lifetime of this instance. No lock coalesces concurrent
    /// callers before the first success: each runs its own <c>--version</c>, and the first to succeed sets the
    /// flag — a few redundant checks while unconfirmed cost little, and <see cref="_versionConfirmed"/> only ever
    /// moves from <see langword="false"/> to <see langword="true"/>, so the race is benign.
    /// </summary>
    private async Task<string?> EnsureVersionAsync(CancellationToken ct)
    {
        if (_versionConfirmed)
        {
            return null;
        }

        var problem = await CheckVersionAsync(ct).ConfigureAwait(false);
        if (problem is null)
        {
            _versionConfirmed = true;
        }

        return problem;
    }

    private async Task<string?> CheckVersionAsync(CancellationToken ct)
    {
        var result = await ExecuteAsync(Directory.GetCurrentDirectory(), ["--version"], null, null, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return $"Could not determine the git version: 'git --version' {(result.TimedOut ? "timed out" : $"exited {result.ExitCode}: {result.StdErr.Trim()}")}.";
        }

        var match = VersionPattern().Match(result.StdOut);
        if (!match.Success || !int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return $"Could not parse a git version from 'git --version' output '{result.StdOut.Trim()}'; git 2.32 or later is required (for GIT_CONFIG_GLOBAL isolation).";
        }

        if (major < 2 || (major == 2 && minor < 32))
        {
            return $"git {major}.{minor} is older than the minimum supported 2.32 (needed for GIT_CONFIG_GLOBAL isolation from host configuration).";
        }

        return null;
    }

    private async Task<GitCliResult> ExecuteAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        IReadOnlyList<string>? extraConfig,
        IReadOnlyList<(string Key, string Value)>? secretConfig,
        CancellationToken ct)
    {
        var startInfo = BuildStartInfo(workingDirectory, args, extraConfig, secretConfig);

        using var process = new Process { StartInfo = startInfo };
        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdOut.Append(e.Data).Append('\n'); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stdErr.Append(e.Data).Append('\n'); };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            // git could not be started at all: it is not installed, or the working directory does not exist. A
            // result, not an exception, like every other way a git call can fail.
            return new GitCliResult(-1, string.Empty, $"fatal: could not start '{_options.GitExecutable}' in '{workingDirectory}': {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return await WaitForExitAsync(process, stdOut, stdErr, ct).ConfigureAwait(false);
    }

    private ProcessStartInfo BuildStartInfo(
        string workingDirectory,
        IReadOnlyList<string> args,
        IReadOnlyList<string>? extraConfig,
        IReadOnlyList<(string Key, string Value)>? secretConfig)
    {
        var startInfo = new ProcessStartInfo(_options.GitExecutable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        Sanitize(startInfo.Environment, secretConfig);

        // Isolation flags come first, so a caller-supplied extraConfig (or, for a same key, nothing here) can never
        // be shadowed by them, and so they apply identically to every invocation regardless of caller.
        AddIsolationFlags(startInfo.ArgumentList);

        if (extraConfig is not null)
        {
            foreach (var entry in extraConfig)
            {
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add(entry);
            }
        }

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return startInfo;
    }

    /// <summary>
    /// The isolation flags every call carries, regardless of caller: <c>core.hooksPath</c>, every
    /// <c>protocol.&lt;name&gt;.allow</c> git recognises pinned explicitly rather than left to the
    /// <c>protocol.allow</c> default, and <c>http.followRedirects=false</c>. See the class remarks for why each one
    /// is here.
    /// </summary>
    private void AddIsolationFlags(Collection<string> argumentList)
    {
        argumentList.Add("-c");
        argumentList.Add($"core.hooksPath={_hooksDirectory}");

        // Every protocol pinned explicitly — never just protocol.allow=never plus the two allowed ones — so a
        // per-protocol override sitting in a repository's own config (protocol.http.allow=always, say) can never
        // widen what protocol.allow=never already closed (fix round 2, ruling: pin each protocol explicitly).
        argumentList.Add("-c");
        argumentList.Add("protocol.allow=never");
        argumentList.Add("-c");
        argumentList.Add("protocol.https.allow=always");
        argumentList.Add("-c");
        argumentList.Add("protocol.file.allow=always");
        argumentList.Add("-c");
        argumentList.Add("protocol.http.allow=never");
        argumentList.Add("-c");
        argumentList.Add("protocol.ext.allow=never");
        argumentList.Add("-c");
        argumentList.Add("protocol.git.allow=never");
        argumentList.Add("-c");
        argumentList.Add("protocol.ssh.allow=never");

        // Fix round 2, ruling (pre-existing, predates any A7 code): an origin that answers a fetch or push with an
        // HTTP redirect makes git re-target the request at the redirect's own host — and, for a POST such as
        // git-receive-pack, resend whatever headers it was carrying, including the unscoped Authorization header
        // secretConfig sets for the *original* URL. http.followRedirects=false refuses to follow any redirect at
        // all: the call fails outright, and the redirect's target host never receives a request, header or not.
        argumentList.Add("-c");
        argumentList.Add("http.followRedirects=false");
    }

    private async Task<GitCliResult> WaitForExitAsync(Process process, StringBuilder stdOut, StringBuilder stdErr, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(_options.CommandTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            Kill(process);
            return new GitCliResult(-1, stdOut.ToString(), stdErr.ToString(), TimedOut: true);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        return new GitCliResult(process.ExitCode, stdOut.ToString(), stdErr.ToString());
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the timeout firing and Kill being called — nothing left to stop.
        }
    }

    private void Sanitize(IDictionary<string, string?> environment, IReadOnlyList<(string Key, string Value)>? secretConfig)
    {
        List<string>? gitVariables = null;
        foreach (var key in environment.Keys)
        {
            if (key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
            {
                (gitVariables ??= []).Add(key);
            }
        }

        if (gitVariables is not null)
        {
            foreach (var key in gitVariables)
            {
                environment.Remove(key);
            }
        }

        environment.Remove("SSH_ASKPASS");
        environment["GIT_TERMINAL_PROMPT"] = "0";
        environment["GCM_INTERACTIVE"] = "Never";
        environment["LC_ALL"] = "C";
        environment["LANGUAGE"] = "C";
        environment["GIT_CONFIG_NOSYSTEM"] = "1";
        environment["GIT_CONFIG_GLOBAL"] = _globalConfigPath;
        environment["HOME"] = _homeDirectory;
        environment["XDG_CONFIG_HOME"] = _homeDirectory;

        if (secretConfig is not { Count: > 0 })
        {
            return;
        }

        environment["GIT_CONFIG_COUNT"] = secretConfig.Count.ToString(CultureInfo.InvariantCulture);
        for (var i = 0; i < secretConfig.Count; i++)
        {
            environment[$"GIT_CONFIG_KEY_{i}"] = secretConfig[i].Key;
            environment[$"GIT_CONFIG_VALUE_{i}"] = secretConfig[i].Value;
        }
    }
}
