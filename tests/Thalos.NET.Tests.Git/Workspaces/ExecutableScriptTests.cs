using System.ComponentModel;
using System.Diagnostics;

namespace Thalos.Tests.Git.Workspaces;

public sealed class ExecutableScriptTests : IDisposable
{
    private const int TextFileBusy = 26; // ETXTBSY
    private const int Rounds = 400;
    private const int Forkers = 8;

    private readonly string _temp = Directory.CreateTempSubdirectory("thalos-exec-script-").FullName;

    /// <summary>
    /// A script written by <see cref="ExecutableScript.Write"/> starts at once even while other threads keep forking
    /// children, as the parallel tests of this assembly do. Each of 400 rounds writes a fresh script and runs it
    /// straight away while eight threads start <c>true</c> in a loop. Written in this process with
    /// <see cref="File.WriteAllText(string, string?)"/>, 67 of 2000 starts failed with "Text file busy" in a two-CPU
    /// Linux container, about 3.4% each, so 400 rounds all pass with probability about 1.6e-6; written through
    /// <c>/bin/sh</c>, none can, because no forked child ever inherits a writable descriptor of the script.
    /// Red: make ExecutableScript.Write write the file with <c>File.WriteAllText</c> and <c>File.SetUnixFileMode</c>
    /// in this process.
    /// </summary>
    [SkippableFact]
    public async Task A_script_written_while_other_threads_fork_starts_at_once()
    {
        Skip.If(OperatingSystem.IsWindows(), "ETXTBSY is a Unix exec rule; Windows has no equivalent.");
        using var stop = new CancellationTokenSource();
        var forkers = Enumerable.Range(0, Forkers).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var child = Process.Start(new ProcessStartInfo("true") { UseShellExecute = false })!;
                await child.WaitForExitAsync();
            }
        })).ToArray();

        var busy = new List<string>();
        try
        {
            for (var round = 0; round < Rounds; round++)
            {
                var script = Path.Combine(_temp, $"script-{round}.sh");
                ExecutableScript.Write(script, "#!/bin/sh\nexit 0\n");
                try
                {
                    using var started = Process.Start(new ProcessStartInfo(script) { UseShellExecute = false })!;
                    await started.WaitForExitAsync();
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == TextFileBusy)
                {
                    busy.Add($"round {round}: {ex.Message}");
                }
            }
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(forkers);
        }

        busy.Should().BeEmpty($"no process may hold a script open for writing when it is started; {busy.Count} of {Rounds} starts failed");
    }

    public void Dispose() => Directory.Delete(_temp, recursive: true);
}
