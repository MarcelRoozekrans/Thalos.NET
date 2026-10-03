using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Thalos.Git.Workspaces;
using Thalos.Sandbox;
using Thalos.Sandbox.Host;
using Thalos.Tests.Git.Workspaces;

namespace Thalos.Tests.Sandbox.Host;

/// <summary>
/// One sandbox host, run in-process on a <see cref="TestServer"/> with its own work root, a counting fake process runner,
/// and the stdio test server standing in for RoslynCodeLens.
/// </summary>
internal sealed class HostHarness : IAsyncDisposable
{
    public const string Token = "sandbox-test-token-0123456789-abcdefghijklmnop";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WebApplication _app;

    private HostHarness(WebApplication app, string workRoot, Guid runId, CountingRunner runner)
    {
        _app = app;
        WorkRoot = workRoot;
        RunId = runId;
        Runner = runner;
        Client = app.GetTestClient();
    }

    /// <summary>The Roslyn stand-in, built next to this test project.</summary>
    public static string ServerDll => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory.Replace("Thalos.NET.Tests.Sandbox.Host", "Thalos.NET.Tests.McpServer", StringComparison.Ordinal),
        "Thalos.NET.Tests.McpServer.dll"));

    public HttpClient Client { get; }

    public string WorkRoot { get; }

    public string RepoRoot => Path.Combine(WorkRoot, "repo");

    public Guid RunId { get; }

    public CountingRunner Runner { get; }

    public IServiceProvider Services => _app.Services;

    /// <summary>Starts a host whose settings are the defaults below, each overridable through <paramref name="settings"/>.</summary>
    public static async Task<HostHarness> StartAsync(string workRoot, IReadOnlyDictionary<string, string>? settings = null, Action<IServiceCollection>? services = null)
    {
        File.Exists(ServerDll).Should().BeTrue($"build tests/Thalos.NET.Tests.McpServer first ({ServerDll})");
        var runId = Guid.NewGuid();
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SandboxEnvironment.RunId] = runId.ToString("D"),
            [SandboxEnvironment.Token] = Token,
            [SandboxEnvironment.WriteExtensions] = "*",
            [SandboxEnvironment.ProtectedPaths] = ".git/;AGENT.md;.gitattributes;.gitmodules;.github/",
            [SandboxSettings.WorkRootKey] = workRoot,
            [SandboxSettings.RoslynCommandKey] = "dotnet",
            [SandboxSettings.RoslynArgsKey] = $"{ServerDll};--ready-tool;list_solutions",
            [SandboxSettings.RoslynReloadKey] = "none",
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            values[key] = value;
        }

        var builder = SandboxHost.CreateBuilder([.. values.Select(kv => $"--{kv.Key}={kv.Value}")]);
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var runner = new CountingRunner();
        builder.Services.Replace(ServiceDescriptor.Singleton<ISandboxProcessRunner>(runner));
        services?.Invoke(builder.Services);
        var app = SandboxHost.Map(builder.Build());
        await app.StartAsync();
        return new HostHarness(app, workRoot, runId, runner);
    }

    /// <summary>Sends one request with the given Authorization header value, or none.</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? authorization = "Bearer " + Token, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        return await Client.SendAsync(request);
    }

    /// <summary>Posts <paramref name="bundle"/> to <c>/control/import</c>.</summary>
    public async Task<HttpResponseMessage> ImportAsync(string bundle, string branch, string commit, string? solution = "App.slnx")
    {
        var query = $"/control/import?branch={Uri.EscapeDataString(branch)}&commit={Uri.EscapeDataString(commit)}"
            + (solution is null ? "" : $"&solution={Uri.EscapeDataString(solution)}");
        var content = new StreamContent(File.OpenRead(bundle));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await SendAsync(HttpMethod.Post, query, content: content);
    }

    public async Task<ReadyBody> ReadyAsync()
    {
        using var response = await SendAsync(HttpMethod.Get, "/control/ready");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ReadyBody>(Json))!;
    }

    /// <summary>Polls <c>/control/ready</c> until Roslyn is no longer pending.</summary>
    public async Task<ReadyBody> WaitSettledAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        while (true)
        {
            var ready = await ReadyAsync();
            if (!string.Equals(ready.Roslyn, "pending", StringComparison.Ordinal) || timeout.IsCancellationRequested)
            {
                return ready;
            }

            await Task.Delay(200);
        }
    }

    /// <summary>Imports a fresh bundle of <paramref name="remote"/>'s main and waits until it settles.</summary>
    public async Task<(string Commit, ReadyBody Ready)> ImportAndSettleAsync(LocalGitRemote remote, string dataRoot, string branch = "run/feature")
    {
        var (bundle, commit) = await BundleAsync(remote, dataRoot);
        using (var response = await ImportAsync(bundle, branch, commit))
        {
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.Accepted);
        }

        return (commit, await WaitSettledAsync());
    }

    /// <summary>A bundle of <paramref name="remote"/>'s main tip, built by <see cref="GitMirrorStore"/> as the API builds it.</summary>
    public static async Task<(string Bundle, string Commit)> BundleAsync(LocalGitRemote remote, string dataRoot)
    {
        var store = new GitMirrorStore(new GitWorkspaceOptions { DataRoot = dataRoot }, NullLogger<GitMirrorStore>.Instance);
        var mirror = await store.PrepareAsync("repo", remote.Url, CancellationToken.None);
        mirror.IsSuccess.Should().BeTrue(mirror.IsFailure ? mirror.Error.Message : "");
        var commit = remote.HeadOf("main");
        var bundle = Path.Combine(dataRoot, $"{Guid.NewGuid():N}.bundle");
        var written = await store.CreateBundleAsync(mirror.Value, commit, bundle, CancellationToken.None);
        written.IsSuccess.Should().BeTrue(written.IsFailure ? written.Error.Message : "");
        return (bundle, commit);
    }

    /// <summary>An MCP client on <c>/mcp/{source}</c>.</summary>
    public async Task<McpClient> ConnectAsync(string source)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(Client.BaseAddress!, $"mcp/{source}"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>(StringComparer.Ordinal) { ["Authorization"] = $"Bearer {Token}" },
            },
            Client,
            NullLoggerFactory.Instance,
            ownsHttpClient: false);
        // As Thalos.Mcp's HttpMcpClientOptions does: a slow first answer is waited for, not taken for an old server and
        // retried as initialize, a fallback that can fail outright.
        return await McpClient.CreateAsync(transport, new McpClientOptions { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan });
    }

    /// <summary>Calls <paramref name="tool"/> on <paramref name="source"/> and returns its text.</summary>
    public async Task<string> CallAsync(string source, string tool, params (string Name, object? Value)[] arguments)
    {
        await using var client = await ConnectAsync(source);
        var result = await client.CallToolAsync(tool, arguments.ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal));
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>The body of <c>/control/ready</c>.</summary>
internal sealed record ReadyBody(bool Imported, string Restore, string? RestoreDetail, string Roslyn, string? Detail);

/// <summary>Records every process the host runs and answers each without running it.</summary>
internal sealed class CountingRunner : ISandboxProcessRunner
{
    public ConcurrentQueue<ProcessSpec> Specs { get; } = new();

    /// <summary>What a <c>dotnet restore</c> returns.</summary>
    public ProcessOutcome RestoreOutcome { get; set; } = new(0, TimedOut: false, "Restore complete.\n");

    public IReadOnlyList<ProcessSpec> Restores => [.. Specs.Where(s => s.Arguments.Count > 0 && string.Equals(s.Arguments[0], "restore", StringComparison.Ordinal))];

    /// <summary>Called with each spec before it is answered, as the process would run: a test can write into its working directory.</summary>
    public Action<ProcessSpec>? OnRun { get; set; }

    public Task<ProcessOutcome> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        Specs.Enqueue(spec);
        OnRun?.Invoke(spec);
        return Task.FromResult(spec.Arguments.Count > 0 && string.Equals(spec.Arguments[0], "restore", StringComparison.Ordinal)
            ? RestoreOutcome
            : new ProcessOutcome(0, TimedOut: false, "Build succeeded.\n"));
    }
}

/// <summary>
/// Runs every spec through the real <see cref="SandboxProcessRunner"/>, with the spec's own environment and working
/// directory, but as the stdio test server's <c>--dump-env</c>, which writes the environment it was started with to
/// <c>&lt;directory&gt;/&lt;first argument&gt;.env</c>, e.g. <c>restore.env</c>.
/// </summary>
internal sealed class EnvDumpRunner(string directory) : ISandboxProcessRunner
{
    private readonly SandboxProcessRunner _real = new();

    public string FileFor(string verb) => Path.Combine(directory, verb + ".env");

    public async Task<ProcessOutcome> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        var dump = spec with { FileName = "dotnet", Arguments = [HostHarness.ServerDll, "--dump-env", FileFor(spec.Arguments[0])] };
        var outcome = await _real.RunAsync(dump, ct);
        return outcome with { FullOutput = outcome.FullOutput + "Restore complete.\nPassed!\n" };
    }
}
