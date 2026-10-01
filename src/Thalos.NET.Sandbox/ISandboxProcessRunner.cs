namespace Thalos.Sandbox;

/// <summary>One process to run: the executable, its arguments as separate argv entries, where, and for how long.</summary>
/// <param name="FileName">The executable. Never a shell command line.</param>
/// <param name="Arguments">Each entry is passed as exactly one argument; nothing is split, quoted or expanded.</param>
/// <param name="WorkingDirectory">The process's working directory.</param>
/// <param name="Timeout">How long it may run before its whole process tree is killed.</param>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, TimeSpan Timeout);

/// <summary>How a process ended.</summary>
/// <param name="ExitCode">The exit code, or null when the process was killed on timeout.</param>
/// <param name="TimedOut">True when the timeout ran out and the process tree was killed.</param>
/// <param name="FullOutput">Everything written to stdout and stderr, interleaved in arrival order.</param>
public sealed record ProcessOutcome(int? ExitCode, bool TimedOut, string FullOutput);

/// <summary>Runs one process to completion or timeout.</summary>
public interface ISandboxProcessRunner
{
    /// <summary>Runs to completion or timeout, killing the whole tree on timeout; captures stdout and stderr interleaved.</summary>
    /// <param name="spec">What to run.</param>
    /// <param name="ct">Cancels the run; the process tree is killed first.</param>
    Task<ProcessOutcome> RunAsync(ProcessSpec spec, CancellationToken ct);
}
