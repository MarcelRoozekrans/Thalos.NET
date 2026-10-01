using System.Diagnostics;
using Thalos.Sandbox;

namespace Thalos.Tests.Sandbox;

public sealed class SandboxProcessRunnerTests
{
    /// <summary>Red: in SandboxProcessRunner.Kill call process.Kill() without entireProcessTree, so the grandchild survives and the pid is still alive.</summary>
    [Fact]
    public async Task A_timed_out_process_tree_is_killed()
    {
        var dir = Directory.CreateTempSubdirectory("sandbox-runner").FullName;
        var pidFile = Path.Combine(dir, "grandchild.pid");
        var grandchildPid = 0;
        try
        {
            var run = new SandboxProcessRunner().RunAsync(SpawnGrandchild(pidFile, dir), CancellationToken.None);
            var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(60)));
            finished.Should().BeSameAs(run, "the runner must return after the timeout");

            var outcome = await run;
            outcome.TimedOut.Should().BeTrue();
            outcome.ExitCode.Should().BeNull();

            File.Exists(pidFile).Should().BeTrue("the child must have started its grandchild before the timeout");
            grandchildPid = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim(), System.Globalization.CultureInfo.InvariantCulture);
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

    private static ProcessSpec SpawnGrandchild(string pidFile, string dir) => OperatingSystem.IsWindows()
        ? new ProcessSpec(
            "powershell",
            ["-NoProfile", "-Command", $"$p = Start-Process powershell -ArgumentList '-NoProfile','-Command','Start-Sleep 60' -PassThru; Set-Content -Path '{pidFile}' -Value $p.Id; Start-Sleep 60"],
            dir,
            TimeSpan.FromSeconds(10))
        : new ProcessSpec("sh", ["-c", $"sleep 60 & echo $! > '{pidFile}'; wait"], dir, TimeSpan.FromSeconds(3));

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
