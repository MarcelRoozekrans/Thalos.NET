using System.Diagnostics;
using System.Text;

namespace Thalos.Sandbox;

/// <summary>Runs a process with a fixed argument list, no shell, killing its whole tree on timeout.</summary>
public sealed class SandboxProcessRunner : ISandboxProcessRunner
{
    /// <summary>How long to wait for output pipes to drain after the tree was killed, so a survivor holding a pipe cannot hang the caller.</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public async Task<ProcessOutcome> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);

        using var process = Start(spec, out var output);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(spec.Timeout);
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

            return new ProcessOutcome(null, TimedOut: true, output.ToString());
        }

        return new ProcessOutcome(process.ExitCode, TimedOut: false, output.ToString());
    }

    private static Process Start(ProcessSpec spec, out OutputBuffer output)
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

        var buffer = new OutputBuffer();
        var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => buffer.AppendLine(e.Data);
        process.ErrorDataReceived += (_, e) => buffer.AppendLine(e.Data);
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        output = buffer;
        return process;
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
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

    /// <summary>Stdout and stderr lines appended in arrival order from the two reader callbacks.</summary>
    private sealed class OutputBuffer
    {
        private readonly StringBuilder _text = new();
        private readonly Lock _gate = new();

        public void AppendLine(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (_gate)
            {
                _text.Append(line).Append('\n');
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                return _text.ToString();
            }
        }
    }
}
