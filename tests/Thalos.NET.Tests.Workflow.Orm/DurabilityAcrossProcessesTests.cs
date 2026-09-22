using System.Diagnostics;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>
/// Proves durability across an actual OS process boundary, not a fresh DI scope: <c>write</c> runs to
/// completion and exits fully before <c>read</c> starts as a brand-new process with nothing shared in memory.
/// A same-process test — a second store instance in the same AppDomain — would pass even if a run's state
/// were sitting in a field somewhere, which is exactly how a compile-time state machine would have fooled a
/// careless test. This cannot pass that way: the only channel between the two invocations is PostgreSQL.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Docker")]
public sealed class DurabilityAcrossProcessesTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_run_resumes_in_a_brand_new_process()
    {
        var write = await RunHostAsync("write", pg.ConnectionString);
        write.ExitCode.Should().Be(0, write.Stderr);
        var runId = write.Stdout.Trim();

        var read = await RunHostAsync("read", pg.ConnectionString, runId);

        read.ExitCode.Should().Be(0, read.Stderr);
        read.Stdout.Should().Contain("current_node=review");
    }

    private static async Task<HostResult> RunHostAsync(params string[] args)
    {
        var hostExe = LocateHostExecutable();
        var startInfo = new ProcessStartInfo(hostExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start '{hostExe}'.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        // Waits for the child process to fully exit — the next RunHostAsync call (a brand-new process) only
        // starts after this one has returned, per the durability proof's requirement.
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new HostResult(process.ExitCode, stdout, stderr);
    }

    private static string LocateHostExecutable()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var targetFramework = Path.GetFileName(baseDir);
        var configDir = Path.GetDirectoryName(baseDir) ?? throw new InvalidOperationException($"Unexpected test output directory layout: '{baseDir}'.");
        var configuration = Path.GetFileName(configDir);
        var binDir = Path.GetDirectoryName(configDir) ?? throw new InvalidOperationException($"Unexpected test output directory layout: '{baseDir}'.");
        var testProjectDir = Path.GetDirectoryName(binDir) ?? throw new InvalidOperationException($"Unexpected test output directory layout: '{baseDir}'.");
        var testsDir = Path.GetDirectoryName(testProjectDir) ?? throw new InvalidOperationException($"Unexpected test output directory layout: '{baseDir}'.");

        var exeName = OperatingSystem.IsWindows() ? "Thalos.NET.Tests.Workflow.Orm.Host.exe" : "Thalos.NET.Tests.Workflow.Orm.Host";
        var hostExe = Path.Combine(testsDir, "Thalos.NET.Tests.Workflow.Orm.Host", "bin", configuration, targetFramework, exeName);

        if (!File.Exists(hostExe))
        {
            throw new FileNotFoundException(
                $"The durability host executable was not found at '{hostExe}'. Build it first: " +
                "dotnet build tests/Thalos.NET.Tests.Workflow.Orm.Host/Thalos.NET.Tests.Workflow.Orm.Host.csproj",
                hostExe);
        }

        return hostExe;
    }

    private sealed record HostResult(int ExitCode, string Stdout, string Stderr);
}
