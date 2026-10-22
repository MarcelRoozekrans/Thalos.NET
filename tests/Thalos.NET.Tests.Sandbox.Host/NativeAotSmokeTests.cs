using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Thalos.Git.Workspaces;
using Thalos.Mcp;
using Thalos.Sandbox;
using Thalos.Sandbox.Host;
using Thalos.Tests.Git.Workspaces;

namespace Thalos.Tests.Sandbox.Host;

/// <summary>
/// The sandbox host published as Native AOT: <c>tests/Thalos.NET.Tests.Sandbox.Host.Native</c>, a consumer's two-line
/// entry point, is published for <c>linux-x64</c> with no warning treated as an error, and the published binary is run
/// with the <c>THALOS_SANDBOX_*</c> environment against a real git bundle.
/// </summary>
/// <remarks>
/// Linux x64 only: a native publish links for the machine it runs on. Each run publishes into a fresh
/// <c>--artifacts-path</c>, because an up-to-date publish prints no warnings and would count zero.
/// </remarks>
public sealed class NativeAotSmokeTests(SandboxHostNativePublish publish) : IClassFixture<SandboxHostNativePublish>, IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("thalos-sbx-native-run-").FullName;

    [SkippableFact]
    public void The_native_publish_reports_no_trim_or_AOT_warning()
    {
        Skip.IfNot(NativeHostPublish.Supported, "the native publish targets linux-x64");
        // Red: look for a command that does not exist instead of clang: the test fails with the install hint, never
        // with a generic publish failure, and never skips.
        publish.MissingToolchain.Should().BeNull();
        // Red: break the smoke host's Program.cs so it does not compile: the publish exits 1. A publish that fails for
        // any reason is never counted as zero warnings.
        publish.ExitCode.Should().Be(0, $"the native publish must succeed; its output ends with:\n{publish.OutputTail}");

        // Red: restore [RequiresUnreferencedCode] and [RequiresDynamicCode] on SandboxHost.CreateBuilder: the publish
        // reports IL2026 and IL3050 at the smoke host's call.
        publish.Warnings.Should().BeEmpty("Thalos.NET.Sandbox.Host is AOT-clean; the publish reported:\n{0}", string.Join('\n', publish.Warnings));
    }

    [SkippableFact]
    public async Task The_native_host_serves_control_ready_and_the_workspace_tools()
    {
        Skip.IfNot(NativeHostPublish.Supported, "the native publish targets linux-x64");
        // Red: look for a command that does not exist instead of clang: the test fails with the install hint, never
        // with a generic publish failure, and never skips.
        publish.MissingToolchain.Should().BeNull();
        // Red: as in the test above, a smoke host that does not compile: the publish exits 1.
        publish.ExitCode.Should().Be(0, $"the native publish must succeed; its output ends with:\n{publish.OutputTail}");

        using var remote = LocalGitRemote.Create(("App.slnx", "<Solution />\n"), ("Smoke.cs", "class NativeSmoke { }\n"));
        var (bundle, commit) = await HostHarness.BundleAsync(remote, Path.Combine(_temp, "mirror-data"));
        await using var host = NativeHost.Start(publish.Binary, Path.Combine(_temp, "work"));
        var http = host.Http;

        // Red: answer /control/ready with Results.Json(report), without ReadyJsonContext: the native host cannot
        // serialize the report and answers 500.
        using (var ready = await host.WaitForReadyAsync())
        {
            ready.StatusCode.Should().Be(HttpStatusCode.OK, host.Log);
        }

        using (var content = new StreamContent(File.OpenRead(bundle)))
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var imported = await http.PostAsync(new Uri($"control/import?branch=run%2Fsmoke&commit={commit}&solution=App.slnx", UriKind.Relative), content);
            // Red: rename ImportAsync's solution parameter, so the generated binding reads another query key: 400.
            imported.StatusCode.Should().Be(HttpStatusCode.Accepted, host.Log);
        }

        await host.WaitForImportAsync();

        await using var client = await McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "mcp/workspace"), TransportMode = HttpTransportMode.StreamableHttp },
                http,
                NullLoggerFactory.Instance,
                ownsHttpClient: false),
            HttpMcpClientOptions.Create());

        // Red: leave read_file out of the workspace tools the sandbox path registers: tools/list lacks it.
        var tools = await client.ListToolsAsync();
        tools.Select(t => t.Name).Should().Contain("read_file", host.Log);

        // Red: drop the host's unwrapping of a string result: the call answers the content as a quoted JSON string.
        var result = await client.CallToolAsync("read_file", new Dictionary<string, object?>(StringComparer.Ordinal) { ["path"] = "Smoke.cs" });
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text)).Should().Be("class NativeSmoke { }\n", host.Log);
    }

    public void Dispose() => NativeHostPublish.DeleteQuietly(_temp);
}

/// <summary>The native smoke host's publish: <c>tests/Thalos.NET.Tests.Sandbox.Host.Native</c>, once per run.</summary>
public sealed class SandboxHostNativePublish() : NativeHostPublish(
    Path.Combine("tests", "Thalos.NET.Tests.Sandbox.Host.Native", "Thalos.NET.Tests.Sandbox.Host.Native.csproj"),
    "Thalos.NET.Tests.Sandbox.Host.Native");

/// <summary>The published native host, run with the <c>THALOS_SANDBOX_*</c> environment on a free loopback port; killed on dispose.</summary>
internal sealed class NativeHost : IAsyncDisposable
{
    public const string Token = "native-smoke-token-0123456789-abcdefghijklmnop";

    private readonly Process _process;
    private readonly StringBuilder _log = new();

    private NativeHost(Process process, int port)
    {
        _process = process;
        Http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public HttpClient Http { get; }

    /// <summary>What the host has written so far, for a failure message.</summary>
    public string Log
    {
        get
        {
            lock (_log)
            {
                return $"native host output:\n{_log}";
            }
        }
    }

    public static NativeHost Start(string binary, string workRoot)
    {
        var port = FreePort();
        var start = new ProcessStartInfo(binary)
        {
            WorkingDirectory = Path.GetDirectoryName(binary)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        start.Environment[SandboxEnvironment.RunId] = Guid.NewGuid().ToString("D");
        start.Environment[SandboxEnvironment.Token] = Token;
        start.Environment[SandboxEnvironment.WriteExtensions] = "*";
        start.Environment[SandboxEnvironment.ProtectedPaths] = ".git/;AGENT.md;.gitattributes;.gitmodules;.github/";
        start.Environment[SandboxSettings.WorkRootKey] = workRoot;
        start.Environment[SandboxSettings.RoslynCommandKey] = "dotnet";
        start.Environment[SandboxSettings.RoslynArgsKey] = $"{HostHarness.ServerDll};--ready-tool;list_solutions";
        start.Environment[SandboxSettings.RoslynReloadKey] = "none";
        return new NativeHost(Process.Start(start) ?? throw new InvalidOperationException("Could not start the native host."), port);
    }

    /// <summary>Polls <c>/control/ready</c> until the host answers it at all.</summary>
    public async Task<HttpResponseMessage> WaitForReadyAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            // Red: build a tool context's options in a static property initializer, which runs before the generated
            // Default is set: the host throws TypeInitializationException at start and exits.
            _process.HasExited.Should().BeFalse($"the native host exited early. {Log}");
            try
            {
                return await Http.GetAsync(new Uri("control/ready", UriKind.Relative));
            }
            catch (HttpRequestException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
        }
    }

    /// <summary>Polls <c>/control/ready</c> until the import has checked the run's commit out.</summary>
    public async Task WaitForImportAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var body = await Http.GetStringAsync(new Uri("control/ready", UriKind.Relative));
            if (body.Contains("\"imported\":true", StringComparison.Ordinal))
            {
                return;
            }

            // Red: drop ImportService's workspaces.Publish call: the import never reports imported.
            DateTime.UtcNow.Should().BeBefore(deadline, $"the import did not finish; /control/ready answered {body}. {Log}");
            await Task.Delay(200);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process.Dispose();
    }

    private void Append(string? line)
    {
        lock (_log)
        {
            _log.AppendLine(line);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
