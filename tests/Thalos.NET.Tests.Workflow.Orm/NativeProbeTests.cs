using System.Diagnostics;
using Xunit.Abstractions;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>
/// Thalos' compile-time paths as real Native AOT output: <c>tests/Thalos.NET.Tests.Native.Probe</c> roots YAML loading,
/// workflow rows and the outbox, tools, options binding and tool authorization, is published for <c>linux-x64</c>, and
/// prints one canonical line per check. The published binary and a JIT <c>dotnet run</c> of the same project, against one
/// PostgreSQL container, must print the same lines.
/// </summary>
/// <remarks>
/// Linux x64 only, with Docker: a native publish links for the machine it runs on, and the rows check needs PostgreSQL.
/// </remarks>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Docker")]
public sealed class NativeProbeTests(PostgresFixture pg, NativeProbePublish publish, ITestOutputHelper output) : IClassFixture<NativeProbePublish>, IDisposable
{
    private readonly string _jitArtifacts = Directory.CreateTempSubdirectory("thalos-native-probe-jit-").FullName;

    /// <summary>The checks the probe prints, in order.</summary>
    private static readonly string[] Checks = ["yaml", "rows", "outbox", "tools", "options", "authz"];

    [SkippableFact]
    public void The_probe_publishes_with_no_warning_from_the_assemblies_it_roots()
    {
        Skip.IfNot(NativeHostPublish.Supported, "the native publish targets linux-x64");
        // Red: in NativeHostPublish, look for a command that does not exist instead of clang: the test fails with the
        // install hint.
        publish.MissingToolchain.Should().BeNull();
        // Red: break the probe's Program.cs so it does not compile: the publish exits 1, never counted as zero warnings.
        publish.ExitCode.Should().Be(0, $"the native publish must succeed; its output ends with:\n{publish.OutputTail}");

        var (thalos, thirdParty) = Partition(publish.Warnings);
        foreach (var warning in thirdParty)
        {
            output.WriteLine($"third-party trim or AOT warning: {warning}");
        }

        // Red: add a JsonSerializer.Serialize(object) call to the probe: the publish reports IL2026 and IL3050 at the
        // probe's own line.
        thalos.Should().BeEmpty("no Thalos assembly or the probe may warn natively; the publish reported:\n{0}", string.Join('\n', thalos));
    }

    [SkippableFact]
    public async Task The_native_probe_prints_what_the_JIT_probe_prints()
    {
        Skip.IfNot(NativeHostPublish.Supported, "the native publish targets linux-x64");
        // Red: as in the test above, in NativeHostPublish look for a command that does not exist instead of clang.
        publish.MissingToolchain.Should().BeNull();
        // Red: as in the test above, a probe that does not compile: the publish exits 1.
        publish.ExitCode.Should().Be(0, $"the native publish must succeed; its output ends with:\n{publish.OutputTail}");

        string[] probeArguments = ["--connection", pg.ConnectionString, "--processes", ProcessesDirectory()];
        var native = await RunAsync(publish.Binary, probeArguments);
        // The JIT run is a consumer's default: PublishAot also sets the AOT feature switches in the runtimeconfig of a
        // plain run, such as IsDynamicCodeSupported and reflection-based JSON off, so the JIT run builds without it, into
        // its own artifacts.
        var jit = await RunAsync("dotnet", [
            "run", "--project", publish.Project, "-c", "Release", "--property:PublishAot=false", "--artifacts-path", _jitArtifacts,
            "--no-launch-profile", "-tl:off", "--", .. probeArguments]);
        output.WriteLine($"native:\n{native.Stdout}");

        // Red: make the probe return 1 when RuntimeFeature.IsDynamicCodeSupported is false: the native run exits 1.
        native.ExitCode.Should().Be(0, native.Stderr);
        // Red: make the probe return 1 when RuntimeFeature.IsDynamicCodeSupported is true: the JIT run exits 1.
        jit.ExitCode.Should().Be(0, jit.Stderr);

        var nativeLines = Lines(native.Stdout);
        // Red: leave the authz check out of the probe: the native run prints five lines, and the list differs.
        nativeLines.Select(l => l.Split(' ', 2)[0]).Should().Equal(Checks, native.Stdout);

        // Red: make the probe print RuntimeFeature.IsDynamicCodeSupported in the yaml line: True on JIT, False natively.
        nativeLines.Should().Equal(Lines(jit.Stdout), "the native probe must print what the JIT probe prints");
    }

    public void Dispose() => NativeHostPublish.DeleteQuietly(_jitArtifacts);

    /// <summary>
    /// The publish's warning keys split by origin: a located warning in the repository, which is Thalos' own source or
    /// the probe's, and an unlocated one whose origin member is in a <c>Thalos</c> namespace, against every other one.
    /// </summary>
    private static (List<string> Thalos, List<string> ThirdParty) Partition(IReadOnlyList<string> warnings)
    {
        var thalos = new List<string>();
        var thirdParty = new List<string>();
        foreach (var key in warnings)
        {
            var parts = key.Split('|', 4);
            var file = parts[1];
            var located = file.Length > 0 && !Path.IsPathRooted(file);
            var thalosOrigin = file.Length == 0 && parts[3].StartsWith("Thalos.", StringComparison.Ordinal);
            (located || thalosOrigin ? thalos : thirdParty).Add(key);
        }

        return (thalos, thirdParty);
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The Daedalus process files Thalos.NET.Tests.Workflow keeps as fixtures.</summary>
    private static string ProcessesDirectory() =>
        Path.Combine(NativeHostPublish.RepoRoot(), "tests", "Thalos.NET.Tests.Workflow", "Fixtures", "processes");

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string command, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(command)
        {
            WorkingDirectory = NativeHostPublish.RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        NativeHostPublish.RemoveInheritedMSBuildSettings(start);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {command}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdout, await stderr);
    }
}

/// <summary>The native probe's publish: <c>tests/Thalos.NET.Tests.Native.Probe</c>, once per run.</summary>
public sealed class NativeProbePublish() : NativeHostPublish(
    Path.Combine("tests", "Thalos.NET.Tests.Native.Probe", "Thalos.NET.Tests.Native.Probe.csproj"),
    "Thalos.NET.Tests.Native.Probe");
