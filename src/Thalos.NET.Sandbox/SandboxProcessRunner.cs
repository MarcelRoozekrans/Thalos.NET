using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Thalos.Sandbox;

/// <summary>Runs a process with a fixed argument list, no shell, an explicit environment, killing its whole tree on timeout.</summary>
public sealed class SandboxProcessRunner : ISandboxProcessRunner
{
    /// <summary>How long to wait for output pipes to drain after the tree was killed, so a survivor holding a pipe cannot hang the caller.</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _clock;

    /// <summary>A runner whose timeouts run on the system clock.</summary>
    public SandboxProcessRunner()
        : this(TimeProvider.System)
    {
    }

    /// <summary>A runner whose <see cref="ProcessSpec.Timeout"/> runs on <paramref name="clock"/>; tests pass a fake one.</summary>
    internal SandboxProcessRunner(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<ProcessOutcome> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var output = new OutputBuffer(spec.MaxOutputChars);
        Process process;
        try
        {
            process = Start(spec, output);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new ProcessOutcome(null, TimedOut: false, "", StartError: ex.Message);
        }

        using var owned = process;
        using var deadline = new CancellationTokenSource(spec.Timeout, _clock);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await DrainAsync(process).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            return new ProcessOutcome(null, TimedOut: true, output.ToString(), ErrorLineCount: output.ErrorLines);
        }

        return new ProcessOutcome(process.ExitCode, TimedOut: false, output.ToString(), ErrorLineCount: output.ErrorLines);
    }

    /// <summary>Starts the process. When any step throws, what already started is killed and disposed before the exception propagates.</summary>
    private static Process Start(ProcessSpec spec, OutputBuffer buffer)
    {
        var info = new ProcessStartInfo(spec.FileName)
        {
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in spec.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // Nothing is inherited: the host's environment holds the sandbox token, and the child may run agent-written code.
        info.Environment.Clear();
        foreach (var (key, value) in spec.Environment ?? SandboxChildEnvironment.Curated())
        {
            info.Environment[key] = value;
        }

        var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => buffer.AppendLine(e.Data);
        process.ErrorDataReceived += (_, e) => buffer.AppendLine(e.Data);
        try
        {
            process.Start();
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }
        catch
        {
            Kill(process);
            process.Dispose();
            throw;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException)
        {
            // Already exited, never started, or only part of the tree could be killed. The caller still drains and reports.
        }
    }

    private static async Task DrainAsync(Process process)
    {
        using var grace = new CancellationTokenSource(DrainGrace);
        try
        {
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A survivor still holds a pipe; what was captured so far is what the caller gets.
        }
    }

    /// <summary>Whether <paramref name="line"/> is an error line: an MSBuild "path: error CODE: text", or one starting "error " after any leading whitespace. The leading-whitespace tolerance is deliberate; the first version required "error " at column 0.</summary>
    internal static bool IsErrorLine(string line) =>
        line.Contains(": error ", StringComparison.OrdinalIgnoreCase) || line.TrimStart().StartsWith("error ", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Stdout and stderr lines appended in arrival order from the two reader callbacks, keeping only the last
    /// <c>max</c> characters and counting error lines as they arrive.
    /// </summary>
    internal sealed class OutputBuffer(int max)
    {
        private readonly StringBuilder _text = new();
        private readonly Lock _gate = new();
        private int _errorLines;

        /// <summary>
        /// How many characters are held right now, before <see cref="ToString"/> trims to the cap. Never more than twice
        /// the cap once an append returns, which is the memory bound; inside one append it briefly holds that plus the
        /// appended line.
        /// </summary>
        internal int RetainedLength
        {
            get
            {
                lock (_gate)
                {
                    return _text.Length;
                }
            }
        }

        public int ErrorLines
        {
            get
            {
                lock (_gate)
                {
                    return _errorLines;
                }
            }
        }

        internal void AppendLine(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (_gate)
            {
                if (IsErrorLine(line))
                {
                    _errorLines++;
                }

                _text.Append(line).Append((char)10);
                if (_text.Length > max * 2L)
                {
                    _text.Remove(0, _text.Length - max);
                }
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                var start = Math.Max(0, _text.Length - max);
                return _text.ToString(start, _text.Length - start);
            }
        }
    }
}
