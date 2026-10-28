using System.Diagnostics;
using System.Text.Json;
using Xunit.Abstractions;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>
/// Thalos' compile-time paths as real Native AOT output: <c>tests/Thalos.NET.Tests.Native.Probe</c> roots YAML loading,
/// workflow rows and the outbox, tools, options binding, tool authorization and AI.Sentinel, is published for
/// <c>linux-x64</c>, and prints one canonical line per check. The published binary and a JIT <c>dotnet run</c> of the same project, against one
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
    private static readonly string[] Checks = ["yaml", "rows", "outbox", "tools", "options", "authz", "sentinel", "anthropic", "ollama"];

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

        // Red: reach YamlDotNet's reflection-based DeserializerBuilder from the probe: ILC reports IL2026, IL2067 and
        // IL3050 inside YamlDotNet, with no location and a YamlDotNet origin.
        thirdParty.Should().BeEmpty("third-party warning: stop and report (F11); the publish reported:\n{0}", string.Join('\n', thirdParty));

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

        // The content the golden must hold at all, so a golden captured from a probe that lost it cannot pass.
        using (var outbox = Check(nativeLines, "outbox"))
        {
            // Red: in the probe's OutboxAsync, keep only the messages of the implement node: one entry is left.
            outbox.RootElement.GetArrayLength().Should().Be(2, "the start and the resume each enqueue a dispatch");
        }

        using (var tools = Check(nativeLines, "tools"))
        {
            // Red: drop git__create_branch from ScriptedModel.Calls: calls holds four results.
            tools.RootElement.GetProperty("calls").EnumerateObject().Count().Should().Be(5, "the turn calls one tool of each local source");
            // Red: in the probe's ToolsAsync, write ToolOracle.Dump([]) as local: the dump is empty.
            tools.RootElement.GetProperty("local").GetString().Should().NotBeNullOrEmpty();
            // Red: in the probe's ToolsAsync, write ToolOracle.Dump([]) as remote: the dump is empty.
            tools.RootElement.GetProperty("remote").GetString().Should().NotBeNullOrEmpty();
        }

        AssertSentinel(nativeLines);
        AssertAdapters(nativeLines);

        var golden = Lines(NativeProbeGolden.Output);
        // Red: make the probe print RuntimeFeature.IsDynamicCodeSupported in the yaml line: False natively, absent from
        // the golden. Red: in WorkflowVariableBlock.ToPlainValue, read an array's numbers with GetDouble: the rows line
        // tags the list's 2 as double. Red: in the probe's NoGitWrites, drop the repository path from the echo.
        nativeLines.Should().Equal(golden, "the native probe must print the JIT golden");
        // Red: make the probe print an extra yaml entry only when RuntimeFeature.IsDynamicCodeSupported is true: the
        // JIT run differs from the golden, the native run does not.
        Lines(jit.Stdout).Should().Equal(golden, "the JIT probe must print the golden it was captured as");
    }

    /// <summary>The content the sentinel line must hold, so a golden captured from a probe that lost it cannot pass.</summary>
    private static void AssertSentinel(string[] nativeLines)
    {
        using var sentinel = Check(nativeLines, "sentinel");
        // Red: in SentinelThalosBuilderExtensions.UseAISentinel, set o.RedactByDefault = false before configure runs: the
        // model is sent the address itself.
        sentinel.RootElement.GetProperty("sent")[0].GetString().Should().Contain("[EMAIL_1_{tag}]").And.NotContain("jane.doe");
        // Red: in the probe's MailFunction, keep the to argument only when it does not contain an @: the tool line is null.
        sentinel.RootElement.GetProperty("tool").GetString().Should().Be("jane.doe@example.com", "PII placeholders are restored before the tool runs");
        // Red: in the probe's SentinelAsync, call UseAISentinel(o => o.RestoreRedactedValues = false): the reply keeps the
        // placeholder, "Mailed [EMAIL_1_<tag>].".
        sentinel.RootElement.GetProperty("reply").GetString().Should().Be("Mailed jane.doe@example.com.", "the reply is restored for the caller");
        // Red: in SentinelErrorMappingChatClient.Map, build the quarantine as AgentError.ProviderError: blocked starts
        // with ProviderError.
        sentinel.RootElement.GetProperty("blocked").GetString().Should().Be("Quarantined Critical: SEC-01");
    }

    /// <summary>The content the adapter lines must hold, so a golden captured from a probe that lost it cannot pass.</summary>
    private static void AssertAdapters(string[] nativeLines)
    {
        // Every Red below was applied to the JIT probe, whose output this test compares, and the printed line changed as
        // named; this test itself needs Docker and a native publish, so it is not run locally.
        using (var anthropic = Check(nativeLines, "anthropic"))
        {
            var call = anthropic.RootElement.GetProperty("call");
            // Red: in AnthropicResponseMapper.ToContent, build the text block's TextContent from string.Empty: call.text is "".
            call.GetProperty("text").GetString().Should().Be("Pong");
            // Red: in AnthropicResponseMapper, ignore TextDelta in the streaming case: stream.text is "".
            // Red: in the probe's RecordedAnthropicHandler, never treat a request as streaming: the stream ends before
            // message_stop and the line is a stream_incomplete error, with no stream entry.
            anthropic.RootElement.GetProperty("stream").GetProperty("text").GetString().Should().Be("1 2 3 4 5");
            // Red: in AnthropicResponseMapper.ToChatResponse, set FinishReason to null: the call's finish is null.
            call.GetProperty("finish").GetString().Should().Be("stop");
            // Red: in AnthropicResponseMapper.ToUsageDetails, set TotalTokenCount to null: total is null.
            call.GetProperty("total").GetInt64().Should().Be(23, "the recording reports 18 input and 5 output tokens");
        }

        using var ollama = Check(nativeLines, "ollama");
        // Red: in OllamaEmbeddingGenerator.GenerateAsync, break after the first embedding: count is 1.
        ollama.RootElement.GetProperty("count").GetInt32().Should().Be(2);
        // Red: in the probe's StubOllamaHandler, answer two-dimensional vectors: the dimensions are 2.
        ollama.RootElement.GetProperty("dimensions")[0].GetInt32().Should().Be(3);
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

    /// <summary>The JSON of the line of <paramref name="check"/>.</summary>
    private static JsonDocument Check(string[] lines, string check) =>
        JsonDocument.Parse(lines.Single(l => l.StartsWith(check + " ", StringComparison.Ordinal))[(check.Length + 1)..]);

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
        await NativeHostPublish.WaitOrKillAsync(process, TimeSpan.FromMinutes(10), stdout, stderr, command);
        return (process.ExitCode, await stdout, await stderr);
    }
}

/// <summary>The native probe's publish: <c>tests/Thalos.NET.Tests.Native.Probe</c>, once per run.</summary>
public sealed class NativeProbePublish() : NativeHostPublish(
    Path.Combine("tests", "Thalos.NET.Tests.Native.Probe", "Thalos.NET.Tests.Native.Probe.csproj"),
    "Thalos.NET.Tests.Native.Probe");
