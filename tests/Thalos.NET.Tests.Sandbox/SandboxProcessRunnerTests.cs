using System.Diagnostics;
using Thalos.Sandbox;

namespace Thalos.Tests.Sandbox;

public sealed class SandboxProcessRunnerTests
{
    /// <summary>
    /// The timeout runs on a fake clock, advanced only once the grandchild's pid file exists, so however slowly the
    /// child starts under load, the tree is complete when it times out. Red 1: in SandboxProcessRunner.Kill call
    /// process.Kill() without entireProcessTree, so the grandchild survives and the pid is still alive. Red 2: time the
    /// run with CancelAfter on the system clock again; the advance then times nothing out and the 30-second bound fails.
    /// </summary>
    [Fact]
    public async Task A_timed_out_process_tree_is_killed()
    {
        var dir = Directory.CreateTempSubdirectory("sandbox-runner").FullName;
        var pidFile = Path.Combine(dir, "grandchild.pid");
        var grandchildPid = 0;
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        // Far longer than any real wait here, so only the advance can time the run out.
        var spec = SpawnGrandchild(pidFile, dir) with { Timeout = TimeSpan.FromMinutes(10) };
        try
        {
            var run = new SandboxProcessRunner(clock).RunAsync(spec, CancellationToken.None);
            grandchildPid = await GrandchildPidAsync(pidFile, run, TimeSpan.FromMinutes(2));
            run.IsCompleted.Should().BeFalse("nothing times out before the clock is advanced");

            clock.Advance(spec.Timeout + TimeSpan.FromSeconds(1));
            var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)));
            finished.Should().BeSameAs(run, "the runner must return once the timeout passes");

            var outcome = await run;
            outcome.TimedOut.Should().BeTrue();
            outcome.ExitCode.Should().BeNull();
            (await IsGoneWithin(grandchildPid, TimeSpan.FromSeconds(10))).Should().BeTrue("the grandchild must be killed with the tree");
        }
        finally
        {
            KillQuietly(grandchildPid);
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Red: read only stdout, or only stderr, in SandboxProcessRunner.Start.</summary>
    [Fact]
    public async Task Output_from_both_streams_and_the_exit_code_are_captured()
    {
        var spec = OperatingSystem.IsWindows()
            ? new ProcessSpec("cmd", ["/c", "echo out& echo err 1>&2& exit 3"], Path.GetTempPath(), TimeSpan.FromSeconds(30))
            : new ProcessSpec("sh", ["-c", "echo out; echo err >&2; exit 3"], Path.GetTempPath(), TimeSpan.FromSeconds(30));

        var outcome = await new SandboxProcessRunner().RunAsync(spec, CancellationToken.None);

        outcome.TimedOut.Should().BeFalse();
        outcome.ExitCode.Should().Be(3);
        outcome.FullOutput.Should().Contain("out").And.Contain("err");
    }

    /// <summary>Red: in SandboxProcessRunner.RunAsync remove the catch around Start, so a missing executable throws.</summary>
    [Fact]
    public async Task An_executable_that_does_not_exist_is_reported_not_thrown()
    {
        var spec = new ProcessSpec("definitely-not-a-real-executable-xyz", [], Path.GetTempPath(), TimeSpan.FromSeconds(30));

        var outcome = await new SandboxProcessRunner().RunAsync(spec, CancellationToken.None);

        outcome.StartError.Should().NotBeNullOrEmpty();
        outcome.ExitCode.Should().BeNull();
        outcome.TimedOut.Should().BeFalse();
    }

    /// <summary>Red: in SandboxProcessRunner.RunAsync remove the catch around Start, so a missing working directory throws.</summary>
    [Fact]
    public async Task A_working_directory_that_does_not_exist_is_reported_not_thrown()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var spec = OperatingSystem.IsWindows()
            ? new ProcessSpec("cmd", ["/c", "echo hi"], missing, TimeSpan.FromSeconds(30))
            : new ProcessSpec("sh", ["-c", "echo hi"], missing, TimeSpan.FromSeconds(30));

        var outcome = await new SandboxProcessRunner().RunAsync(spec, CancellationToken.None);

        outcome.StartError.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// 2000 error lines then a summary line, with a 10000 character cap. Red: make OutputBuffer.ToString return the first
    /// max characters instead of the last, which turns the EndWith and newest-lines assertions red; count error lines from
    /// the retained text instead of as lines arrive, which turns the ErrorLineCount assertion red. The memory bound itself
    /// is asserted by <see cref="The_buffer_never_holds_more_than_twice_the_cap"/>, not here, because ToString trims on read.
    /// </summary>
    [Fact]
    public async Task Output_beyond_the_cap_is_bounded_keeps_the_end_and_still_counts_dropped_errors()
    {
        // About 40000 characters, four times the cap. The ten-minute limit is only a safety bound: the run takes seconds.
        const int lines = 2000;
        var spec = OperatingSystem.IsWindows()
            ? new ProcessSpec("cmd", ["/c", $"(for /L %i in (1,1,{lines}) do @echo f.cs: error E %i) & echo Passed!"], Path.GetTempPath(), TimeSpan.FromMinutes(10), MaxOutputChars: 10000)
            : new ProcessSpec("sh", ["-c", $"i=1; while [ $i -le {lines} ]; do echo \"f.cs: error E $i\"; i=$((i+1)); done; echo Passed!"], Path.GetTempPath(), TimeSpan.FromMinutes(10), MaxOutputChars: 10000);

        var outcome = await new SandboxProcessRunner().RunAsync(spec, CancellationToken.None);

        outcome.TimedOut.Should().BeFalse();
        outcome.FullOutput.Length.Should().BeLessThanOrEqualTo(10000);
        outcome.FullOutput.TrimEnd().Should().EndWith("Passed!");
        outcome.FullOutput.Should().Contain($"error E {lines}", "the newest lines survive, not the oldest");
        outcome.ErrorLineCount.Should().Be(lines);
    }

    /// <summary>
    /// Once each append returns, the buffer holds at most twice the cap, as RetainedLength documents. Red 1: remove the
    /// Remove call that trims in OutputBuffer.AppendLine, so the buffer grows without bound. Red 2: trim only past twice
    /// the cap plus 100, so an append can return holding more than twice the cap.
    /// </summary>
    [Fact]
    public void The_buffer_never_holds_more_than_twice_the_cap()
    {
        var buffer = new SandboxProcessRunner.OutputBuffer(1000);

        for (var i = 0; i < 5000; i++)
        {
            buffer.AppendLine(new string('x', 99));
            buffer.RetainedLength.Should().BeLessThanOrEqualTo(2 * 1000);
        }

        buffer.ToString().Length.Should().BeLessThanOrEqualTo(1000);
    }

    private static ProcessSpec SpawnGrandchild(string pidFile, string dir) => OperatingSystem.IsWindows()
        ? new ProcessSpec(
            "powershell",
            ["-NoProfile", "-Command", $"$p = Start-Process powershell -ArgumentList '-NoProfile','-Command','Start-Sleep 60' -PassThru; Set-Content -Path '{pidFile}' -Value $p.Id; Start-Sleep 60"],
            dir,
            TimeSpan.FromSeconds(10))
        : new ProcessSpec("sh", ["-c", $"sleep 60 & echo $! > '{pidFile}'; wait"], dir, TimeSpan.FromSeconds(3));

    /// <summary>Waits for the pid file the child writes once the grandchild runs, failing if the run ends first or the limit passes.</summary>
    private static async Task<int> GrandchildPidAsync(string pidFile, Task<ProcessOutcome> run, TimeSpan limit)
    {
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < limit)
        {
            run.IsCompleted.Should().BeFalse("the run ended before its child started the grandchild");
            if (File.Exists(pidFile))
            {
                try
                {
                    if (int.TryParse((await File.ReadAllTextAsync(pidFile)).Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid) && pid > 0)
                    {
                        return pid;
                    }
                }
                catch (IOException)
                {
                    // Still being written.
                }
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"the child did not start its grandchild within {limit}");
    }

    private static async Task<bool> IsGoneWithin(int pid, TimeSpan limit)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < limit)
        {
            if (IsGone(pid))
            {
                return true;
            }

            await Task.Delay(100);
        }

        return IsGone(pid);
    }

    private static bool IsGone(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static void KillQuietly(int pid)
    {
        if (pid == 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Already gone.
        }
    }
}
