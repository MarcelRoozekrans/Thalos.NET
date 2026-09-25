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
/// between calls, except the one-time git version check and the isolation files created in the constructor.
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
/// tracked config might name cannot exist to run) and <c>-c protocol.allow=never -c protocol.https.allow=always
/// -c protocol.file.allow=always</c> (so only the two transports this provider actually uses work; everything else,
/// including <c>ext::</c>, is refused by git itself regardless of what any config layer says). Removing the global
/// and system config layers is the root fix; the hooksPath and protocol flags are stated explicitly on the command
/// line as well because they are cheap to state and give defense in depth against a future caller who reintroduces
/// a config source this type does not control.
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
    private readonly string _globalConfigPath;
    private readonly Lazy<Task<string?>> _versionProblem;

    public GitCli(GitWorkspaceOptions options)
    {
        _options = options;

        var isolationDirectory = Path.Combine(options.DataRoot, ".git-isolation");
        _hooksDirectory = Path.Combine(isolationDirectory, "hooks");
        _globalConfigPath = Path.Combine(isolationDirectory, "global.config");

        Directory.CreateDirectory(_hooksDirectory);
        if (!File.Exists(_globalConfigPath))
        {
            File.Create(_globalConfigPath).Dispose();
        }

        _versionProblem = new Lazy<Task<string?>>(CheckVersionAsync, LazyThreadSafetyMode.ExecutionAndPublication);
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

        var versionProblem = await _versionProblem.Value.ConfigureAwait(false);
        if (versionProblem is not null)
        {
            return new GitCliResult(1, string.Empty, versionProblem);
        }

        return await ExecuteAsync(workingDirectory, args, extraConfig, secretConfig, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <c>git --version</c> once (memoized in <see cref="_versionProblem"/>) and reports why it is unusable,
    /// or <see langword="null"/> when it is at least 2.32 — the version <c>GIT_CONFIG_GLOBAL</c> needs, which is
    /// this type's whole host-isolation mechanism (see the class remarks).
    /// </summary>
    private async Task<string?> CheckVersionAsync()
    {
        var result = await ExecuteAsync(Directory.GetCurrentDirectory(), ["--version"], null, null, CancellationToken.None).ConfigureAwait(false);
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

        process.Start();
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
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"core.hooksPath={_hooksDirectory}");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("protocol.allow=never");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("protocol.https.allow=always");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("protocol.file.allow=always");

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
