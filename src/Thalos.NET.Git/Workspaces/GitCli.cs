using System.Diagnostics;
using System.Text;

namespace Thalos.Git.Workspaces;

/// <summary>The exit code, standard output and standard error of one git invocation.</summary>
internal readonly record struct GitCliResult(int ExitCode, string StdOut, string StdErr)
{
    /// <summary><see langword="true"/> when <see cref="ExitCode"/> is 0.</summary>
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Runs the git executable as a real child <see cref="Process"/>, awaited with
/// <see cref="Process.WaitForExitAsync(CancellationToken)"/> — never LibGit2Sharp, and never <see cref="Task.Run{TResult}(Func{TResult})"/>
/// to fake async over a synchronous API (ruling R23). Every call is independent; nothing is cached or kept open
/// between calls.
/// </summary>
/// <remarks>
/// <para>
/// <b>Arguments.</b> <see cref="RunAsync"/> takes its <c>args</c> as a list appended verbatim to
/// <see cref="ProcessStartInfo.ArgumentList"/> — never a concatenated command string — so nothing a caller passes
/// (a branch name, a remote URL, a run id) is ever interpreted by a shell. Each <c>extraConfig</c> entry becomes a
/// separate <c>-c &lt;entry&gt;</c> pair placed before <c>args</c>, which is how <see cref="GitWorktreeWorkspaceProvider"/>
/// passes a credential header and disables symlinks on a checkout without writing either to the repository's
/// persisted config.
/// </para>
/// <para>
/// <b>Environment.</b> The child starts from the host process's own environment — so <c>PATH</c>, <c>TEMP</c>, and
/// on Windows <c>SYSTEMROOT</c>, everything the git executable itself needs to run, are inherited — and then every
/// inherited <c>GIT_*</c> variable is stripped: any one of them (<c>GIT_DIR</c>, <c>GIT_WORK_TREE</c>,
/// <c>GIT_SSH_COMMAND</c>, <c>GIT_CONFIG_GLOBAL</c>, ...) could silently redirect an operation the caller believes
/// is confined to <see cref="RunAsync"/>'s own <c>workingDirectory</c>. <c>SSH_ASKPASS</c> is stripped too, since it
/// names an external program git will launch the same way a terminal prompt would ask. Four variables are then set
/// explicitly: <c>GIT_TERMINAL_PROMPT=0</c>, so a missing credential fails the command instead of blocking
/// indefinitely on a prompt that never comes; <c>GCM_INTERACTIVE=Never</c>, because Git Credential Manager — the
/// default credential helper on Git for Windows — can still raise its own GUI prompt even with terminal prompting
/// off; and <c>LC_ALL=C</c> / <c>LANGUAGE=C</c>, so git's own messages are in English and stable across hosts —
/// <see cref="GitWorktreeWorkspaceProvider"/> reads <see cref="GitCliResult.StdErr"/>'s first line into
/// <see cref="AgentError.Detail"/> and scrubs a credential header out of it first, and a localized message would
/// undermine both.
/// </para>
/// <para>
/// <b>Timeout.</b> <see cref="GitWorkspaceOptions.CommandTimeout"/> bounds a single invocation; on expiry the whole
/// process tree is killed and a <see cref="TimeoutException"/> is thrown — a caller that hangs on
/// <c>GIT_TERMINAL_PROMPT=0</c> alone (a credential helper that does not honor it, or a stalled network read) is
/// still bounded. Cancelling the caller's own token kills the process the same way and lets the resulting
/// <see cref="OperationCanceledException"/> propagate rather than becoming a <see cref="TimeoutException"/>.
/// </para>
/// </remarks>
internal sealed class GitCli(GitWorkspaceOptions options)
{
    /// <summary>Runs <c>git [-c &lt;extraConfig&gt;]* &lt;args&gt;</c> in <paramref name="workingDirectory"/>.</summary>
    /// <param name="workingDirectory">The directory git runs in — equivalent to a leading <c>git -C &lt;workingDirectory&gt;</c>.</param>
    /// <param name="args">The subcommand and its arguments, one <see cref="ProcessStartInfo.ArgumentList"/> entry each.</param>
    /// <param name="extraConfig">
    /// Zero or more <c>key=value</c> config overrides, each passed as its own <c>-c</c> flag on the command line
    /// only — never written to a config file. <see langword="null"/> or empty passes none.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<GitCliResult> RunAsync(string workingDirectory, IReadOnlyList<string> args, IReadOnlyList<string>? extraConfig, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(args);

        var startInfo = new ProcessStartInfo(options.GitExecutable)
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

        Sanitize(startInfo.Environment);

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

        using var process = new Process { StartInfo = startInfo };
        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdOut.Append(e.Data).Append('\n'); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stdErr.Append(e.Data).Append('\n'); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(options.CommandTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            Kill(process);
            throw new TimeoutException($"'git {string.Join(' ', args)}' did not finish within {options.CommandTimeout}.", ex);
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

    private static void Sanitize(IDictionary<string, string?> environment)
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
    }
}
