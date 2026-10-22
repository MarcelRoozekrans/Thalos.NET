using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Thalos.Tests;

/// <summary>
/// Publishes one native host project once per run, with the command of Daedalus's AOT survey; the project sets
/// PublishAot itself. The publish goes into a fresh <c>--artifacts-path</c>, because an up-to-date publish prints no
/// warnings and would count zero. A test project derives one fixture per host, naming the project and its binary.
/// Lives in Thalos.NET.Tests.Sandbox.Host and is shared with the other test projects as a linked file, with
/// <see cref="IlWarnings"/>.
/// </summary>
/// <param name="project">The host's project file, relative to the repository root.</param>
/// <param name="binaryName">The published binary's file name, which is the project's assembly name.</param>
public abstract class NativeHostPublish(string project, string binaryName) : IAsyncLifetime
{
    private readonly string _temp = Directory.CreateTempSubdirectory("thalos-native-").FullName;
    private string _output = "";

    /// <summary>Whether this machine can publish and run the host: a native publish links for the machine it runs on.</summary>
    public static bool Supported => OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.X64;

    public int ExitCode { get; private set; } = -1;

    /// <summary>Why the publish was not attempted on Linux x64, or null when the toolchain is there.</summary>
    public string? MissingToolchain { get; private set; }

    /// <summary>The distinct trim and AOT warning keys of the publish, as <see cref="IlWarnings.Distinct"/> makes them.</summary>
    public IReadOnlyList<string> Warnings { get; private set; } = [];

    public string OutputTail => _output.Length > 6000 ? _output[^6000..] : _output;

    public string Binary => Path.Combine(_temp, "publish", binaryName);

    /// <summary>The host's project file, absolute.</summary>
    public string Project => Path.Combine(RepoRoot(), project);

    public async Task InitializeAsync()
    {
        if (!Supported)
        {
            return;
        }

        // A native publish links with clang against zlib; without clang the publish fails deep in ILC's targets.
        if (FindOnPath("clang") is null)
        {
            MissingToolchain = "clang is not on PATH, so Native AOT cannot link on this machine. Install the toolchain, "
                + "for example `sudo apt-get install -y clang zlib1g-dev`, as ci.yml does on its Linux leg.";
            return;
        }

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in (string[])[
            "publish", project,
            "-c", "Release", "-r", "linux-x64",
            "-p:TreatWarningsAsErrors=false", "-p:WarningsAsErrors=", "-p:TrimmerSingleWarn=false",
            "--artifacts-path", Path.Combine(_temp, "artifacts"), "-o", Path.Combine(_temp, "publish"), "-tl:off", "--disable-build-servers"])
        {
            start.ArgumentList.Add(argument);
        }

        RemoveInheritedMSBuildSettings(start);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet publish.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await WaitOrKillAsync(process, TimeSpan.FromMinutes(20), stdout, stderr, "dotnet publish");
        _output = await stdout + await stderr;
        ExitCode = process.ExitCode;
        Warnings = IlWarnings.Distinct(_output, RepoRoot());
    }

    public Task DisposeAsync()
    {
        DeleteQuietly(_temp);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Waits for <paramref name="process"/> to exit. Past <paramref name="limit"/> it kills the process tree, so no
    /// child build or host outlives the test, and throws <see cref="TimeoutException"/> with the end of the output.
    /// </summary>
    internal static async Task WaitOrKillAsync(Process process, TimeSpan limit, Task<string> stdout, Task<string> stderr, string what)
    {
        using var timeout = new CancellationTokenSource(limit);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            var output = await stdout + await stderr;
            var tail = output.Length > 6000 ? output[^6000..] : output;
            throw new TimeoutException($"{what} did not exit within {limit}; it was killed. Its output ends with:\n{tail}", ex);
        }
    }

    /// <summary>The test runner's own MSBuild settings must not leak into a child build.</summary>
    internal static void RemoveInheritedMSBuildSettings(ProcessStartInfo start)
    {
        var inherited = start.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var name in inherited)
        {
            start.Environment.Remove(name);
        }
    }

    internal static void DeleteQuietly(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // best effort: a child process may still hold a file for a moment
        }
        catch (UnauthorizedAccessException)
        {
            // best effort
        }
    }

    internal static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Thalos.NET.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Thalos.NET.slnx not found above the test output.");
    }

    private static string? FindOnPath(string command) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, command))
            .FirstOrDefault(File.Exists);
}
