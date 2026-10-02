using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Git.Workspaces;
using Thalos.Sandbox;
using Thalos.Sandbox.Host;
using Thalos.Tests.Git.Workspaces;
using Thalos.Workspaces;

namespace Thalos.Tests.Sandbox;

/// <summary>
/// One real sandbox host, run in-process on loopback Kestrel, so an <c>HttpClientTransport</c> reaches it over a real
/// socket. Its settings are passed as <c>--KEY=value</c> arguments, which <see cref="SandboxHost.CreateBuilder"/> reads
/// over the environment, so no test changes the process environment. Restore goes to a fake runner; the Roslyn server is
/// the stdio test server. A middleware ahead of the bearer check records every request this sandbox receives.
/// </summary>
internal sealed class LoopbackSandbox : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WebApplication _app;
    private readonly HttpClient _http = new();
    private readonly Hooks _hooks;
    private bool _stopped;

    private LoopbackSandbox(WebApplication app, Guid runId, string token, Uri baseAddress, ConcurrentQueue<string> requests, Hooks hooks)
    {
        _app = app;
        Requests = requests;
        _hooks = hooks;
        RunId = runId;
        Token = token;
        BaseAddress = baseAddress;
    }

    /// <summary>The stdio test server, built next to this test project.</summary>
    public static string ServerDll => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory.Replace("Thalos.NET.Tests.Sandbox", "Thalos.NET.Tests.McpServer", StringComparison.Ordinal),
        "Thalos.NET.Tests.McpServer.dll"));

    public Guid RunId { get; }

    public string Token { get; }

    public Uri BaseAddress { get; }

    /// <summary>Every request received, as <c>&lt;path&gt; &lt;JSON-RPC method or tool&gt;</c>, including those the bearer check refuses.</summary>
    public ConcurrentQueue<string> Requests { get; }

    /// <summary>
    /// The MCP handshakes received, one per client connected: <c>initialize</c>, or <c>server/discover</c>, which the SDK
    /// sends to a stateless server instead.
    /// </summary>
    public int Connects => Requests.Count(r => r.EndsWith(" initialize", StringComparison.Ordinal) || r.EndsWith(" server/discover", StringComparison.Ordinal));

    /// <summary>The <c>tools/call</c> requests received, as tool names.</summary>
    public IReadOnlyList<string> ToolCalls => [.. Requests.Where(r => r.Contains(" call:", StringComparison.Ordinal)).Select(r => r[(r.IndexOf(" call:", StringComparison.Ordinal) + 6)..])];

    /// <summary>
    /// When set, a <c>tools/call</c> of a tool it returns a value for is answered, ahead of the bearer check and the MCP
    /// endpoint, with a JSON-RPC response whose <c>result</c> is that raw JSON: an untrusted sandbox's malformed answer.
    /// </summary>
    public Func<string, string?>? AnswerToolCall
    {
        get => _hooks.AnswerToolCall;
        set => _hooks.AnswerToolCall = value;
    }

    /// <summary>
    /// When set, a <c>tools/call</c> of a tool it returns a writer for is answered, ahead of the bearer check and the MCP
    /// endpoint, by that writer, given the request's raw id and the response: for answers framed or paced in a way
    /// <see cref="AnswerToolCall"/> cannot express, such as server-sent events or a slow drip.
    /// </summary>
    public Func<string, Func<string, HttpResponse, Task>?>? RespondToToolCall
    {
        get => _hooks.RespondToToolCall;
        set => _hooks.RespondToToolCall = value;
    }

    /// <summary>Called, ahead of the bearer check, when an MCP handshake arrives: while a client is connecting.</summary>
    public Action? OnHandshake
    {
        get => _hooks.OnHandshake;
        set => _hooks.OnHandshake = value;
    }

    /// <summary>
    /// How many more requests are answered 502 ahead of everything else, as the gateway answers while a new container's
    /// host is not yet listening.
    /// </summary>
    public int BadGatewayAnswers
    {
        get => _hooks.BadGatewayAnswers;
        set => _hooks.BadGatewayAnswers = value;
    }

    /// <summary>When set, <c>POST /control/export</c> is answered 503 ahead of the bearer check, as a failing export.</summary>
    public bool RefuseExport
    {
        get => _hooks.RefuseExport;
        set => _hooks.RefuseExport = value;
    }

    /// <summary>How long a <c>dotnet build</c> takes; it honours the call's token.</summary>
    public TimeSpan BuildDelay
    {
        get => _hooks.BuildDelay;
        set => _hooks.BuildDelay = value;
    }

    /// <summary>The endpoint of the sandbox's <c>/mcp/{source}</c> route, with its token or <paramref name="token"/>.</summary>
    public RunToolEndpoint Endpoint(string source, string? token = null) => new(new Uri(BaseAddress, $"mcp/{source}"), token ?? Token);

    /// <summary>Starts a host for <paramref name="runId"/> with <paramref name="token"/>, or a new run and a token of its own.</summary>
    public static async Task<LoopbackSandbox> StartAsync(string workRoot, Guid? runId = null, string? token = null)
    {
        File.Exists(ServerDll).Should().BeTrue($"build tests/Thalos.NET.Tests.McpServer first ({ServerDll})");
        var run = runId ?? Guid.NewGuid();
        return await StartCoreAsync(workRoot, run, token ?? $"sandbox-{run:N}-token");
    }

    private static async Task<LoopbackSandbox> StartCoreAsync(string workRoot, Guid runId, string token)
    {
        string[] args =
        [
            $"--{SandboxEnvironment.RunId}={runId:D}",
            $"--{SandboxEnvironment.Token}={token}",
            $"--{SandboxEnvironment.WriteExtensions}=*",
            $"--{SandboxEnvironment.ProtectedPaths}=.git/;AGENT.md",
            $"--{SandboxSettings.WorkRootKey}={workRoot}",
            $"--{SandboxSettings.RoslynCommandKey}=dotnet",
            $"--{SandboxSettings.RoslynArgsKey}={ServerDll};--ready-tool;list_solutions",
            $"--{SandboxSettings.RoslynReloadKey}=none",
        ];

        var builder = SandboxHost.CreateBuilder(args);
        builder.Logging.ClearProviders();
        var hooks = new Hooks();
        builder.Services.Replace(ServiceDescriptor.Singleton<ISandboxProcessRunner>(new FakeRunner(hooks)));
        var app = builder.Build();
        app.Urls.Clear();
        app.Urls.Add("http://127.0.0.1:0");
        var requests = new ConcurrentQueue<string>();
        app.Use(async (context, next) =>
        {
            if (hooks.TakeBadGatewayAnswer())
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response.WriteAsync("<html>502 Bad Gateway</html>");
                return;
            }

            if (hooks.RefuseExport && context.Request.Path.Equals("/control/export", StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("export refused by the test");
                return;
            }

            var (description, id, tool) = await DescribeAsync(context.Request);
            requests.Enqueue($"{context.Request.Path} {description}");
            if (description is "initialize" or "server/discover")
            {
                hooks.OnHandshake?.Invoke();
            }
            if (tool is not null && id is not null && await AnsweredByHookAsync(hooks, tool, id, context.Response))
            {
                return;
            }

            await next(context);
        });
        SandboxHost.Map(app);
        await app.StartAsync();
        return new LoopbackSandbox(app, runId, token, new Uri(app.Urls.Single().TrimEnd('/') + "/"), requests, hooks);
    }

    /// <summary>Answers a <c>tools/call</c> by <see cref="RespondToToolCall"/> or <see cref="AnswerToolCall"/>, if either has an answer for the tool.</summary>
    private static async Task<bool> AnsweredByHookAsync(Hooks hooks, string tool, string id, HttpResponse response)
    {
        if (hooks.RespondToToolCall?.Invoke(tool) is { } respond)
        {
            await respond(id, response);
            return true;
        }

        if (hooks.AnswerToolCall?.Invoke(tool) is { } result)
        {
            response.ContentType = "application/json";
            await response.WriteAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{result}}}");
            return true;
        }

        return false;
    }

    /// <summary>Imports <paramref name="remote"/>'s main and waits until Roslyn is ready.</summary>
    public async Task ImportAsync(LocalGitRemote remote, string dataRoot)
    {
        var store = new GitMirrorStore(new GitWorkspaceOptions { DataRoot = dataRoot }, NullLogger<GitMirrorStore>.Instance);
        var mirror = await store.PrepareAsync("repo", remote.Url, CancellationToken.None);
        mirror.IsSuccess.Should().BeTrue(mirror.IsFailure ? mirror.Error.Message : "");
        var commit = remote.HeadOf("main");
        var bundle = Path.Combine(dataRoot, $"{Guid.NewGuid():N}.bundle");
        (await store.CreateBundleAsync(mirror.Value, commit, bundle, CancellationToken.None)).IsSuccess.Should().BeTrue();

        using (var content = new StreamContent(File.OpenRead(bundle)))
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var import = await SendAsync(HttpMethod.Post, $"control/import?branch=run%2Ffeature&commit={commit}&solution=App.slnx", content);
            import.StatusCode.Should().Be(System.Net.HttpStatusCode.Accepted);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        while (true)
        {
            using var response = await SendAsync(HttpMethod.Get, "control/ready");
            var ready = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
            var roslyn = ready.GetProperty("roslyn").GetString();
            if (!string.Equals(roslyn, "pending", StringComparison.Ordinal))
            {
                roslyn.Should().Be("ready", ready.ToString());
                return;
            }

            timeout.IsCancellationRequested.Should().BeFalse("the import settles within two minutes");
            await Task.Delay(200);
        }
    }

    /// <summary>Stops the host, so its port refuses connections.</summary>
    public async Task StopAsync()
    {
        if (!_stopped)
        {
            _stopped = true;
            await _app.StopAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await StopAsync();
        await _app.DisposeAsync();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(BaseAddress, path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return await _http.SendAsync(request);
    }

    /// <summary>
    /// The JSON-RPC method of an MCP request, with the tool's name for a <c>tools/call</c>, and the request's raw id and
    /// tool name; an empty description for anything else.
    /// </summary>
    private static async Task<(string Description, string? Id, string? Tool)> DescribeAsync(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method) || !request.Path.StartsWithSegments("/mcp", StringComparison.Ordinal))
        {
            return (string.Empty, null, null);
        }

        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        request.Body.Position = 0;
        try
        {
            using var json = JsonDocument.Parse(body);
            var method = json.RootElement.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
            var id = json.RootElement.TryGetProperty("id", out var i) ? i.GetRawText() : null;
            if (!string.Equals(method, "tools/call", StringComparison.Ordinal))
            {
                return (method, id, null);
            }

            var tool = json.RootElement.GetProperty("params").GetProperty("name").GetString();
            return ("call:" + tool, id, tool);
        }
        catch (JsonException)
        {
            return ("unparsed", null, null);
        }
    }

    /// <summary>What a test changes in a running sandbox.</summary>
    private sealed class Hooks
    {
        public Func<string, string?>? AnswerToolCall { get; set; }

        public Func<string, Func<string, HttpResponse, Task>?>? RespondToToolCall { get; set; }

        public TimeSpan BuildDelay { get; set; }

        public bool RefuseExport { get; set; }

        private int _badGatewayAnswers;

        public int BadGatewayAnswers
        {
            get => Volatile.Read(ref _badGatewayAnswers);
            set => Volatile.Write(ref _badGatewayAnswers, value);
        }

        /// <summary>Takes one of the 502 answers left, if any.</summary>
        public bool TakeBadGatewayAnswer()
        {
            int left;
            do
            {
                left = Volatile.Read(ref _badGatewayAnswers);
                if (left <= 0)
                {
                    return false;
                }
            }
            while (Interlocked.CompareExchange(ref _badGatewayAnswers, left - 1, left) != left);

            return true;
        }

        public Action? OnHandshake { get; set; }
    }

    /// <summary>Answers every process as a success without running it, a build after <see cref="Hooks.BuildDelay"/>.</summary>
    private sealed class FakeRunner(Hooks hooks) : ISandboxProcessRunner
    {
        public async Task<ProcessOutcome> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            if (spec.Arguments.Count > 0 && string.Equals(spec.Arguments[0], "build", StringComparison.Ordinal) && hooks.BuildDelay > TimeSpan.Zero)
            {
                await Task.Delay(hooks.BuildDelay, ct);
            }

            return new ProcessOutcome(0, TimedOut: false, "Restore complete.\n");
        }
    }
}
