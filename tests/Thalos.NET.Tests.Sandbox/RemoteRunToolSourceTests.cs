using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Thalos.Mcp;
using Thalos.Runtime;
using Thalos.Sandbox;
using Thalos.Tests.Git.Workspaces;
using Thalos.Workspaces;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos.Tests.Sandbox;

/// <summary>
/// A9: <see cref="RemoteRunToolSource"/> over real sandbox hosts on loopback Kestrel. The <c>workspace</c> and
/// <c>sandbox</c> sources are registered with <see cref="SandboxToolSourceExtensions.AddRemoteRunTools"/>; the
/// <c>roslyn</c> source is a remote <see cref="McpServerDefinition.RunScoped"/> entry whose host server is the stdio test
/// server, started with <c>--host</c> so its <c>args</c> tool names it. A fake resolver maps each run to its sandbox.
/// </summary>
public sealed class RemoteRunToolSourceTests : IAsyncLifetime
{
    private readonly string _temp = Directory.CreateTempSubdirectory("thalos-remote-run-").FullName;
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly List<LocalGitRemote> _remotes = [];
    private readonly FakeResolver _resolver = new();
    private readonly ConcurrentQueue<string> _logLines = new();

    // ---------- routing ----------

    /// <summary>
    /// Red 1: in RoutedRemoteTool, send a run caller of an MCP-host source to base.InvokeCoreAsync, the host fallback;
    /// the roslyn answer then names --host. Red 2: send a run caller of a local-schema source to the schema function;
    /// list_files then answers that the turn has no run workspace. Red 3: in ConnectAsync, omit the Authorization header;
    /// the sandbox answers 401 and both calls are error results. Red 4: log endpoint.BearerToken in LogConnecting; the
    /// captured log then holds the token. Red 5: resolve the endpoint for Name + "x"; the resolver then records that name.
    /// Red 6: in TextOf, give a TextContent's type name rather than its text; the recorded text of the MCP-host call then
    /// differs from what the agent received.
    /// </summary>
    [Fact]
    public async Task A_run_caller_reaches_its_sandbox()
    {
        var a = await ImportedSandboxAsync("a");
        var recorder = new RecordingObserver();
        await using var sp = Services(observers: [recorder]);

        using (BeginTurn(RunCaller(a.RunId)))
        {
            var listed = await CallAsync(sp, "workspace", "list_files");
            var args = await CallAsync(sp, "roslyn", "args");

            using var _ = new AssertionScope();
            listed.Should().Contain("Marker_a.cs");
            args.Should().Contain(Path.Combine(_temp, "a", "repo")).And.NotContain("--host");
            recorder.Calls.Select(c => c.ResultText).Should().Equal(listed, args);
        }

        a.ToolCalls.Should().Equal("list_files", "args");
        LogText.Should().NotContain(a.Token, "the bearer token is never logged");
        _resolver.Resolved.Should().Equal((a.RunId, "workspace"), (a.RunId, "roslyn"));
    }

    /// <summary>
    /// Red: in ClientForAsync, key the cache by source name rather than by run id, as `_clients.GetOrAdd(Guid.Empty, …)`;
    /// run B's call then evicts run A's client, and A's next call connects again. With the endpoint comparison also
    /// removed, B's call is served by A's sandbox and lists A's files.
    /// </summary>
    [Fact]
    public async Task A_caller_with_another_runs_claim_never_reaches_this_sandbox()
    {
        var a = await ImportedSandboxAsync("a");
        var b = await SandboxAsync("b");
        await using var sp = Services();

        var first = await CallAsAsync(sp, a.RunId, "workspace", "list_files");
        var other = await CallAsAsync(sp, b.RunId, "workspace", "list_files");
        var again = await CallAsAsync(sp, a.RunId, "workspace", "list_files");

        using var _ = new AssertionScope();
        first.Should().Contain("Marker_a.cs");
        again.Should().Contain("Marker_a.cs");
        other.Should().Be("error: this turn has no run workspace", "run B's own sandbox, which imported nothing, answered");
        a.ToolCalls.Should().Equal("list_files", "list_files");
        b.ToolCalls.Should().Equal("list_files");
        a.Connects.Should().Be(1, "run A's client is cached by its run id and survives run B's call");
        b.Connects.Should().Be(1);
    }

    /// <summary>
    /// Red 1: in RoutedRemoteTool, send a caller with no run claim to base.InvokeCoreAsync for local schemas too; the
    /// schema function then answers that the turn has no run workspace. Red 2: treat an invalid claim as no claim; it is
    /// then refused with the no-run text. Red 3: route a caller with no run claim through InvokeForRunAsync as run
    /// Guid.Empty; the resolver is then asked.
    /// </summary>
    [Fact]
    public async Task A_caller_without_a_run_is_refused_for_local_schema_sources()
    {
        await using var sp = Services();
        const string refusal = "error: 'workspace' tools are only available inside a workflow run.";

        var outside = await CallAsync(sp, "workspace", "list_files");
        string noClaim, invalid;
        using (BeginTurn(new TestCaller("chat-user")))
        {
            noClaim = await CallAsync(sp, "workspace", "list_files");
        }

        using (BeginTurn(new TestCaller("forged", new Dictionary<string, string>(StringComparer.Ordinal) { [RunWorkspaceClaims.RunId] = "not-a-guid" })))
        {
            invalid = await CallAsync(sp, "workspace", "list_files");
        }

        using var _ = new AssertionScope();
        outside.Should().Be(refusal);
        noClaim.Should().Be(refusal);
        invalid.Should().Be("error: run tool server 'workspace' is not available for this run: the caller's run claim is not a valid run id.");
        _resolver.Resolved.Should().BeEmpty();
    }

    /// <summary>Red: in RoutedRemoteTool, refuse a caller with no run claim for an MCP-host source too; args then answers with the refusal.</summary>
    [Fact]
    public async Task A_caller_without_a_run_reaches_the_host_server_for_MCP_host_sources()
    {
        await using var sp = Services();

        var outside = await CallAsync(sp, "roslyn", "args");
        string chat;
        using (BeginTurn(new TestCaller("chat-user")))
        {
            chat = await CallAsync(sp, "roslyn", "args");
        }

        using var _ = new AssertionScope();
        outside.Should().Contain("--host");
        chat.Should().Contain("--host");
        _resolver.Resolved.Should().BeEmpty();
    }

    /// <summary>
    /// Red 1: in ClientForAsync, rethrow the connection's exception after logging it; the 401's HttpRequestException then
    /// escapes the call. Red 2: log the endpoint's token when connecting; the log then holds the wrong token. Red 3:
    /// remove RunToolEndpoint's ToString override; the record's text then holds the token. The happy path's red for the
    /// header itself is in <see cref="A_run_caller_reaches_its_sandbox"/>.
    /// </summary>
    [Fact]
    public async Task A_wrong_token_is_an_error_result_not_an_exception()
    {
        var a = await SandboxAsync("a");
        const string wrong = "wrong-token-0123456789-0123456789-abcdef";
        _resolver.Map[a.RunId] = source => a.Endpoint(source, wrong);
        await using var sp = Services();

        var result = await CallAsAsync(sp, a.RunId, "workspace", "list_files");

        using var _ = new AssertionScope();
        result.Should().Be("error: the run's sandbox did not answer 'list_files': no connection could be made to it.");
        a.Requests.Should().NotBeEmpty("the request reached the sandbox and was refused there");
        a.ToolCalls.Should().BeEmpty();
        LogText.Should().NotContain(wrong);
        new RunToolEndpoint(new Uri("http://sandbox/"), wrong).ToString().Should().NotContain(wrong, "the endpoint's text leaves the token out");
    }

    /// <summary>
    /// A sandbox that takes longer than the MCP SDK's 5-second <c>server/discover</c> probe to answer its first handshake,
    /// as a cold or loaded one does, is waited for within ConnectTimeout and connected with that handshake. It is not
    /// taken for a server that predates <c>server/discover</c> and sent <c>initialize</c>, a fallback that can fail the
    /// connection outright. Red 1: in ConnectAsync, pass <c>clientOptions: null</c> to McpClient.CreateAsync; the probe
    /// then times out, and the sandbox also receives initialize and notifications/initialized. Red 2: in
    /// HttpMcpClientOptions.Create, pin ProtocolVersion to 2026-07-28 instead of lifting the probe timeout; the timed-out
    /// probe then fails the connection, and the call is answered that no connection could be made.
    /// </summary>
    [Fact]
    public async Task A_sandbox_slow_to_answer_its_first_handshake_is_waited_for_not_taken_for_an_old_server()
    {
        var a = await SandboxAsync("a");
        var slow = 1;
        a.OnHandshake = async ct =>
        {
            if (Interlocked.Exchange(ref slow, 0) == 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(6), ct);
            }
        };
        await using var sp = Services();

        var result = await CallAsAsync(sp, a.RunId, "workspace", "list_files");

        using var _ = new AssertionScope();
        result.Should().Be("error: this turn has no run workspace", "the sandbox, which imported nothing, answered the call");
        a.Requests.Should().Equal("/mcp/workspace server/discover", "/mcp/workspace call:list_files");
    }

    /// <summary>
    /// Red 1: in OnRemovingAsync, remember the run as removed but do not take its client out; the call after the run is
    /// ready again then reuses the client, and the sandbox sees one handshake. Red 2: in McpThalosBuilderExtensions.AddRemote, do not register the source as an
    /// IRunWorkspaceObserver; the test then finds no observer for it.
    /// </summary>
    [Fact]
    public async Task A_removed_run_drops_its_client()
    {
        var a = await SandboxAsync("a");
        await using var sp = Services();
        await CallAsAsync(sp, a.RunId, "workspace", "list_files");
        await CallAsAsync(sp, a.RunId, "workspace", "list_files");
        a.Connects.Should().Be(1, "the run's client is cached");

        var observer = sp.GetServices<IRunWorkspaceObserver>().OfType<RemoteRunToolSource>().Should().ContainSingle(s => string.Equals(s.Name, "workspace", StringComparison.Ordinal)).Subject;
        var workspace = Workspace(a);
        await observer.OnRemovingAsync(workspace, CancellationToken.None);
        await observer.OnRemovingAsync(workspace, CancellationToken.None); // a repeat is a no-op
        var removed = await CallAsAsync(sp, a.RunId, "workspace", "list_files");
        await observer.OnReadyAsync(workspace, CancellationToken.None);
        await CallAsAsync(sp, a.RunId, "workspace", "list_files");

        using var _ = new AssertionScope();
        removed.Should().EndWith("the run has no sandbox (it may be parked or removed).", "a removed run is refused until it is ready again");
        a.Connects.Should().Be(2, "the removal dropped the client, so the call after the run was ready again connected anew");
    }

    /// <summary>
    /// Red 1: register AddRemoteRunTools("workspace", …) over a copy of WorkspaceTools with one [Description] changed; the
    /// local schema then differs from the one the sandbox lists. Red 2: in RemoteRunToolSource.GetToolsAsync, wrap the
    /// local schemas after Skip(1); the local tool names are then one short of the listed ones.
    /// </summary>
    [Fact]
    public async Task Local_schemas_match_what_the_sandbox_lists()
    {
        var a = await SandboxAsync("a");
        await using var sp = Services();

        await AssertSchemasMatchAsync(sp, a, RunWorkspaceToolOptions.SourceName);
        await AssertSchemasMatchAsync(sp, a, SandboxToolOptions.SourceName);
    }

    // ---------- bounds ----------

    /// <summary>
    /// Red 1: in CallAsync, link the call to the caller's token only, not to the call timeout; slow then answers after its
    /// 30 seconds. Red 2: in AddMcpServer, build the remote entry's options without its CallTimeout; the registered 20
    /// minutes then apply, and slow answers after its 30 seconds.
    /// </summary>
    [Fact]
    public async Task A_remote_MCP_call_past_its_entrys_call_timeout_is_an_error_result()
    {
        var a = await ImportedSandboxAsync("a");
        await using var sp = Services(roslyn: new RunScopedMcpDefinition { Remote = true, CallTimeout = TimeSpan.FromSeconds(2) });

        var result = await CallAsAsync(sp, a.RunId, "roslyn", "slow", ("ms", 30_000));

        result.Should().Be("error: the run's sandbox did not answer 'slow' within 00:00:02; the call was cancelled.");
    }

    /// <summary>
    /// Red: in AddRemoteRunTools, build the options without the registered ones, as new RemoteRunToolOptions; the 20-minute
    /// default then applies, and the build answers after its 30 seconds.
    /// </summary>
    [Fact]
    public async Task A_local_schema_call_past_the_registered_call_timeout_is_an_error_result()
    {
        var a = await ImportedSandboxAsync("a");
        a.BuildDelay = TimeSpan.FromSeconds(30);
        await using var sp = Services(new RemoteRunToolOptions { CallTimeout = TimeSpan.FromSeconds(2) });

        var result = await CallAsAsync(sp, a.RunId, "sandbox", "build");

        result.Should().Be("error: the run's sandbox did not answer 'build' within 00:00:02; the call was cancelled.");
    }

    // ---------- untrusted answers, failing resolvers ----------

    /// <summary>Red: in InvokeForRunAsync, remove the resolve path's final catch; the resolver's exception then escapes.</summary>
    [Fact]
    public async Task A_throwing_resolver_is_an_error_result_not_an_exception()
    {
        var runId = Guid.NewGuid();
        _resolver.Map[runId] = _ => throw new InvalidOperationException("resolver boom");
        await using var sp = Services();

        var result = await CallAsAsync(sp, runId, "workspace", "list_files");

        using var _ = new AssertionScope();
        result.Should().Be("error: run tool server 'workspace' is not available for this run: its endpoint could not be resolved.");
        LogText.Should().Contain("could not resolve the endpoint").And.Contain("InvalidOperationException");
    }

    /// <summary>
    /// Red 1: in CallAsync, remove the final catch; the JsonException of reading a result that is not a call result then
    /// escapes. Red 2: keep the catch but do not drop the client; the next call then reuses it and the sandbox sees one
    /// handshake.
    /// </summary>
    [Fact]
    public async Task A_malformed_answer_is_an_error_result_and_its_client_is_dropped()
    {
        var a = await SandboxAsync("a");
        a.AnswerToolCall = _ => "42";
        await using var sp = Services();

        var local = await CallAsAsync(sp, a.RunId, "workspace", "list_files");
        var host = await CallAsAsync(sp, a.RunId, "roslyn", "args");
        var connectsAfterMalformed = a.Connects;
        a.AnswerToolCall = null;
        var next = await CallAsAsync(sp, a.RunId, "workspace", "list_files");

        using var _ = new AssertionScope();
        local.Should().Be("error: the run's sandbox gave no usable answer to 'list_files'; the call did not complete.");
        host.Should().Be("error: the run's sandbox gave no usable answer to 'args'; the call did not complete.");
        next.Should().Be("error: this turn has no run workspace", "the sandbox's own answer, through a new client");
        a.Connects.Should().Be(connectsAfterMalformed + 1, "the client that read the malformed answer was dropped");
    }

    /// <summary>
    /// Red 1: in ConnectAsync, pass long.MaxValue to ResponseByteCap in place of MaxResultBytes; both oversized answers are
    /// then read whole and returned. Red 2: drop the DropAsync in CallAsync's cap catch; the next call then reuses the
    /// client and the sandbox sees no new handshake.
    /// </summary>
    [Fact]
    public async Task An_answer_longer_than_the_cap_is_an_error_result_and_its_client_is_dropped()
    {
        var a = await SandboxAsync("a");
        var oversized = JsonSerializer.Serialize(new string('x', 256 * 1024));
        a.AnswerToolCall = _ => $"{{\"content\":[{{\"type\":\"text\",\"text\":{oversized}}}]}}";
        await using var sp = Services(new RemoteRunToolOptions { MaxResultBytes = 64 * 1024 });

        var local = await CallAsAsync(sp, a.RunId, "workspace", "list_files");
        var host = await CallAsAsync(sp, a.RunId, "roslyn", "args");
        var connectsAfterOversized = a.Connects;
        a.AnswerToolCall = null;
        var next = await CallAsAsync(sp, a.RunId, "workspace", "list_files");

        using var _ = new AssertionScope();
        local.Should().Be("error: the run's sandbox answered 'list_files' with more than 65536 bytes; the call did not complete.");
        host.Should().Be("error: the run's sandbox answered 'args' with more than 65536 bytes; the call did not complete.");
        next.Should().Be("error: this turn has no run workspace", "an answer under the cap, through a new client");
        a.Connects.Should().Be(connectsAfterOversized + 1, "the client that read the oversized answer was dropped");
        LogText.Should().Contain("with more than 65536 bytes");
    }

    /// <summary>
    /// The real sandbox host answers a stateless Streamable HTTP POST as server-sent events, not JSON. An oversized answer
    /// framed that way must end the call promptly with the cap error, not after the call timeout. Red: in
    /// ConnectAsync, build the HTTP client over the SocketsHttpHandler alone, without ResponseByteCap; both answers are
    /// then read whole and returned.
    /// </summary>
    [Fact]
    public async Task An_event_stream_answer_longer_than_the_cap_fails_promptly_with_the_cap_error()
    {
        var a = await SandboxAsync("a");
        var oversized = JsonSerializer.Serialize(new string('x', 256 * 1024));
        a.RespondToToolCall = _ => (id, response) => WriteEventAsync(response, $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"content\":[{{\"type\":\"text\",\"text\":{oversized}}}]}}}}");
        await using var sp = Services(new RemoteRunToolOptions { MaxResultBytes = 64 * 1024, CallTimeout = TimeSpan.FromMinutes(2) });
        var timer = System.Diagnostics.Stopwatch.StartNew();

        var local = await CallAsAsync(sp, a.RunId, "workspace", "list_files");
        var host = await CallAsAsync(sp, a.RunId, "roslyn", "args");

        using var _ = new AssertionScope();
        local.Should().Be("error: the run's sandbox answered 'list_files' with more than 65536 bytes; the call did not complete.");
        host.Should().Be("error: the run's sandbox answered 'args' with more than 65536 bytes; the call did not complete.");
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "the cap ends the call, not the two-minute call timeout");
    }

    /// <summary>
    /// An answer under the cap that drips and then stalls mid-body is still cut off by the call timeout. Red: in
    /// CappedContent, drop the CancellationToken overloads of SerializeToStreamAsync and CreateContentReadStreamAsync, so
    /// the copy runs without the call's token; the call then waits for the stalled body until the test's bound.
    /// </summary>
    [Fact]
    public async Task A_stalled_answer_under_the_cap_is_cut_off_by_the_call_timeout()
    {
        var a = await SandboxAsync("a");
        a.RespondToToolCall = _ => async (id, response) =>
        {
            response.ContentType = "application/json";
            await response.WriteAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"content\":[{{\"type\":\"text\",\"text\":\"partial");
            await response.Body.FlushAsync();
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2), response.HttpContext.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                // the client went away
            }
        };
        await using var sp = Services(new RemoteRunToolOptions { CallTimeout = TimeSpan.FromSeconds(2) });

        var call = CallAsAsync(sp, a.RunId, "workspace", "list_files");
        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(60)));

        finished.Should().BeSameAs(call, "the call timeout interrupts the stalled copy");
        (await call).Should().Be("error: the run's sandbox did not answer 'list_files' within 00:00:02; the call was cancelled.");
    }

    /// <summary>
    /// One run's calls share a client. When one call's answer exceeds the cap, its client is dropped, which cuts off
    /// another call in flight on it; that other call did not receive an oversized answer and must not say so. Red: make
    /// ResponseByteCap.Watch return one shared ResponseCapHit; the cut-off call is then reported as too large.
    /// </summary>
    [Fact]
    public async Task A_call_cut_off_by_another_calls_oversized_answer_is_not_reported_as_too_large()
    {
        var a = await SandboxAsync("a");
        var oversized = JsonSerializer.Serialize(new string('x', 256 * 1024));
        a.RespondToToolCall = tool => tool switch
        {
            "list_files" => async (_, response) =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(2), response.HttpContext.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    // the client went away
                }
            }
            ,
            "read_file" => (id, response) => WriteEventAsync(response, $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"content\":[{{\"type\":\"text\",\"text\":{oversized}}}]}}}}"),
            _ => null,
        };
        await using var sp = Services(new RemoteRunToolOptions { MaxResultBytes = 64 * 1024, CallTimeout = TimeSpan.FromMinutes(2) });

        var waiting = CallAsAsync(sp, a.RunId, "workspace", "list_files");
        // Longer than the 30-second ConnectTimeout, so a slow connect ends the call with its own error, which the
        // check below reports, before this wait gives up.
        var seen = Stopwatch.StartNew();
        while (!a.ToolCalls.Contains("list_files", StringComparer.Ordinal))
        {
            if (waiting.IsCompleted)
            {
                throw new InvalidOperationException($"the list_files call ended before the sandbox saw it: {await waiting}");
            }

            seen.Elapsed.Should().BeLessThan(TimeSpan.FromMinutes(2), "the sandbox must see the list_files call");
            await Task.Delay(50);
        }

        var oversizedCall = await CallAsAsync(sp, a.RunId, "workspace", "read_file", ("path", "a.cs"));
        var cutOff = await waiting.WaitAsync(TimeSpan.FromMinutes(2));

        using var _ = new AssertionScope();
        oversizedCall.Should().Be("error: the run's sandbox answered 'read_file' with more than 65536 bytes; the call did not complete.");
        cutOff.Should().StartWith("error:").And.NotContain("with more than", "only the call whose own answer exceeded the cap says so");
    }

    /// <summary>Red: drop the MaxResultBytes check in the constructor; the source is then built with a cap of zero.</summary>
    [Fact]
    public void A_cap_that_is_not_positive_is_refused()
    {
        var act = () => RemoteRunToolSource.ForLocalSchemas(
            "x",
            new EmptySchemas(),
            _resolver,
            new RemoteRunToolOptions { MaxResultBytes = 0 },
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            TimeProvider.System);

        act.Should().Throw<ArgumentException>().WithMessage("*MaxResultBytes 0*");
    }

    // ---------- clients a removal or the disposal could miss ----------

    /// <summary>
    /// Red 1: in OnRemovingAsync, do not remember the run as removed; the call then connects and is answered. Red 2: in
    /// ClientForAsync, skip the check before GetOrAdd; the call then connects before the check after it drops the client.
    /// Red 3: make OnReadyAsync a no-op; the run then stays refused after it is ready again.
    /// </summary>
    [Fact]
    public async Task A_run_removed_while_its_endpoint_resolves_is_refused_and_connects_nothing()
    {
        var a = await SandboxAsync("a");
        await using var sp = Services();
        var source = Source(sp, "workspace");
        var workspace = Workspace(a);
        _resolver.BeforeAnswer = async runId => await source.OnRemovingAsync(workspace, CancellationToken.None);

        var removed = await CallAsAsync(sp, a.RunId, "workspace", "list_files");
        var connectsWhileRemoved = a.Connects;
        _resolver.BeforeAnswer = null;
        await source.OnReadyAsync(workspace, CancellationToken.None);
        var ready = await CallAsAsync(sp, a.RunId, "workspace", "list_files");

        using var _ = new AssertionScope();
        removed.Should().Be("error: run tool server 'workspace' is not available for this run: the run has no sandbox (it may be parked or removed).");
        connectsWhileRemoved.Should().Be(0);
        ready.Should().Be("error: this turn has no run workspace", "a run made ready again is served again");
    }

    /// <summary>
    /// Red: in ClientForAsync, skip the check after the client exists; the call then goes on with the client the removal
    /// disposed, and is not refused as removed.
    /// </summary>
    [Fact]
    public async Task A_run_removed_while_its_client_connects_drops_that_client()
    {
        var a = await SandboxAsync("a");
        await using var sp = Services();
        var source = Source(sp, "workspace");
        Task? removal = null;
        a.OnHandshake = _ =>
        {
            // Started, not awaited: the removal waits for this very connection to finish.
            removal ??= source.OnRemovingAsync(Workspace(a), CancellationToken.None).AsTask();
            return Task.CompletedTask;
        };

        var result = await CallAsAsync(sp, a.RunId, "workspace", "list_files");
        await removal!;

        using var _ = new AssertionScope();
        result.Should().Be("error: run tool server 'workspace' is not available for this run: the run has no sandbox (it may be parked or removed).");
        a.ToolCalls.Should().BeEmpty();
        LogText.Should().Contain($"disposed a client of run {a.RunId}");
    }

    /// <summary>Red: in Gone, drop the disposed check; the call then tries to connect and fails as a connection error.</summary>
    [Fact]
    public async Task A_call_after_the_source_is_disposed_is_refused_without_connecting()
    {
        var a = await SandboxAsync("a");
        await using var sp = Services();
        var source = Source(sp, "workspace");
        var tools = (await source.GetToolsAsync(CancellationToken.None)).Value.Cast<AIFunction>().ToDictionary(f => f.Name, StringComparer.Ordinal);
        await source.DisposeAsync();

        string result;
        using (BeginTurn(RunCaller(a.RunId)))
        {
            result = (await tools["list_files"].InvokeAsync(Arguments()))!.ToString()!;
        }

        using var _ = new AssertionScope();
        result.Should().Be("error: run tool server 'workspace' is not available for this run: the tool source has been shut down.");
        a.Connects.Should().Be(0);
    }

    /// <summary>
    /// Red: in ClientForAsync, drop the endpoint comparison; the second call then goes to the first sandbox with the old
    /// client, and nothing is disposed.
    /// </summary>
    [Fact]
    public async Task A_rotated_endpoint_uses_a_new_client_and_disposes_the_old_one()
    {
        var first = await SandboxAsync("first");
        var second = await SandboxAsync("second");
        var runId = Guid.NewGuid();
        _resolver.Map[runId] = source => first.Endpoint(source);
        await using var sp = Services();

        await CallAsAsync(sp, runId, "workspace", "list_files");
        LogText.Should().NotContain($"disposed a client of run {runId}");
        _resolver.Map[runId] = source => second.Endpoint(source);
        await CallAsAsync(sp, runId, "workspace", "list_files");

        using var _ = new AssertionScope();
        first.ToolCalls.Should().Equal("list_files");
        second.ToolCalls.Should().Equal("list_files");
        second.Connects.Should().Be(1);
        LogText.Should().Contain($"disposed a client of run {runId}", "the first sandbox's client was disposed");
    }

    /// <summary>
    /// Red 1: in CallAsync, drop HttpRequestException from the closed-transport catch; the call to the stopped sandbox is
    /// then reported as an unusable answer, not as stopped. Red 2: in StoppedDuringAsync, do not drop the client; the next call then reuses the dead client and is
    /// reported as stopped again rather than as unable to connect.
    /// </summary>
    [Fact]
    public async Task A_stopped_sandbox_is_an_error_result_not_an_exception()
    {
        var a = await SandboxAsync("a");
        await using var sp = Services();
        (await CallAsAsync(sp, a.RunId, "workspace", "list_files")).Should().Be("error: this turn has no run workspace");

        await a.StopAsync();
        var stopped = await CallAsAsync(sp, a.RunId, "workspace", "list_files");
        var next = await CallAsAsync(sp, a.RunId, "workspace", "list_files");

        using var _ = new AssertionScope();
        stopped.Should().Be("error: the run's sandbox stopped during 'list_files'; the call did not complete.");
        next.Should().Be("error: the run's sandbox did not answer 'list_files': no connection could be made to it.", "the dead client was dropped, and a new one cannot connect");
    }

    /// <summary>Red: in InvokeForRunAsync, skip the null check on the resolved endpoint; the call then throws.</summary>
    [Fact]
    public async Task A_run_with_no_sandbox_is_refused()
    {
        await using var sp = Services();
        var runId = Guid.NewGuid();

        var result = await CallAsAsync(sp, runId, "sandbox", "build");

        result.Should().Be("error: run tool server 'sandbox' is not available for this run: the run has no sandbox (it may be parked or removed).");
    }

    // ---------- observers ----------

    /// <summary>
    /// Red 1: in NotifyAsync, let the observer's exception propagate; the call then throws. Red 2: notify the observers
    /// on the no-sandbox refusal too; the recorder then sees two calls. Red 3: do not notify; the recorder sees nothing.
    /// Red 4: do not log an observer's failure; the log then lacks it. Reds 5 to 9, one at a time in the RunToolCall that
    /// InvokeForRunAsync builds: pass Guid.Empty as RunId, "x" as Source, tool.Name + "x" as Tool, a new caller as Caller,
    /// TimeSpan.Zero as Elapsed; the matching assertion then fails.
    /// </summary>
    [Fact]
    public async Task An_observer_sees_each_completed_call_and_cannot_change_its_result()
    {
        var a = await SandboxAsync("a");
        var recorder = new RecordingObserver();
        await using var sp = Services(observers: [new ThrowingObserver(), recorder]);
        var caller = RunCaller(a.RunId);

        string result;
        using (BeginTurn(caller))
        {
            result = await CallAsync(sp, "sandbox", "build");
        }

        var refused = await CallAsync(sp, "sandbox", "build");
        var noSandbox = await CallAsAsync(sp, Guid.NewGuid(), "sandbox", "build");

        using var _ = new AssertionScope();
        result.Should().Be("error: this turn has no run workspace", "the sandbox's own answer, unchanged by the throwing observer");
        refused.Should().StartWith("error: 'sandbox' tools are only available");
        noSandbox.Should().EndWith("the run has no sandbox (it may be parked or removed).");
        var call = recorder.Calls.Should().ContainSingle().Subject;
        call.RunId.Should().Be(a.RunId);
        call.Source.Should().Be("sandbox");
        call.Tool.Should().Be("build");
        call.Caller.Should().BeSameAs(caller);
        call.ResultText.Should().Be(result);
        call.Elapsed.Should().BePositive();
        LogText.Should().Contain("observer failed");
    }

    /// <summary>
    /// Red 1: in NotifyAsync, await the observer without the WaitAsync bound; the call then waits for the hanging observer
    /// forever, and the test's own 30-second bound fails it. Red 2: drop the CancelAsync in the TimeoutException catch;
    /// when the wait ends first, the observer's token is then never cancelled.
    /// </summary>
    [Fact]
    public async Task A_hanging_observer_delays_a_call_only_by_the_observer_timeout()
    {
        var a = await SandboxAsync("a");
        var recorder = new RecordingObserver();
        var hanging = new HangingObserver();
        await using var sp = Services(new RemoteRunToolOptions { ObserverTimeout = TimeSpan.FromMilliseconds(300) }, observers: [hanging, recorder]);

        var result = await CallAsAsync(sp, a.RunId, "workspace", "list_files").WaitAsync(TimeSpan.FromSeconds(30));

        using var _ = new AssertionScope();
        result.Should().Be("error: this turn has no run workspace");
        recorder.Calls.Should().ContainSingle("the observer after the hanging one is still told");
        LogText.Should().Contain("did not finish within 00:00:00.3000000");
        hanging.TokenCancelled.Task.IsCompleted.Should().BeTrue("the late observer is told to stop through its token");
    }

    // ---------- the schema-only tool types ----------

    /// <summary>
    /// Red 1: in AddRemoteRunTools, build the schema container's IRunWorkspaceProvider from the host's container; read_file
    /// then returns the secret, write_file is refused by the allow-list rather than for having no workspace, and build
    /// reaches the throwing runner. Red 2: Red 1 plus AllowAnyWriteExtension in the schema container's options; write_file
    /// then creates New.cs.
    /// </summary>
    [Fact]
    public async Task A_schema_function_invoked_directly_finds_no_workspace_and_touches_no_file()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, "host-workspace")).FullName;
        await File.WriteAllTextAsync(Path.Combine(root, "Secret.cs"), "the secret");
        var runId = Guid.NewGuid();
        await using var sp = Services(provider: new OneWorkspace(new RunWorkspace(runId, "repo", "remote", "main", "run/x", root, null)));
        var workspace = await SchemaFunctionsAsync(sp, "workspace");
        var sandbox = await SchemaFunctionsAsync(sp, "sandbox");

        string read, write;
        using (BeginTurn(RunCaller(runId)))
        {
            read = (await workspace["read_file"].InvokeAsync(Arguments(("path", "Secret.cs"))))!.ToString()!;
            write = (await workspace["write_file"].InvokeAsync(Arguments(("path", "New.cs"), ("content", "x"))))!.ToString()!;
        }

        using (new AssertionScope())
        {
            read.Should().Be("error: this turn has no run workspace");
            write.Should().Be("error: this turn has no run workspace");
            Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).Should().Equal("Secret.cs");
        }

        using (BeginTurn(RunCaller(runId)))
        {
            (await sandbox["build"].InvokeAsync(Arguments()))!.ToString().Should().Be("error: this turn has no run workspace", "the throwing runner is never reached");
        }
    }

    // ---------- registration ----------

    /// <summary>
    /// Red 1: drop the ThrowIfInvalidRemote call in AddMcpServer. Red 2: register a remote entry with the registry as
    /// before. Red 3: in ToolSourceNames.ThrowIfTakenForRemote, drop the remote-name check; a second remote source is then
    /// refused only by the MCP-name check, whose message names an MCP server.
    /// </summary>
    [Fact]
    public async Task A_remote_entry_is_a_remote_source_without_the_registry_and_may_not_describe_a_local_copy()
    {
        await using var sp = Services();

        using (new AssertionScope())
        {
            Source(sp, "roslyn").Should().BeOfType<RemoteRunToolSource>();
            sp.GetService<RunMcpServerRegistry>().Should().BeNull();
            sp.GetServices<IRunWorkspaceObserver>().OfType<RemoteRunToolSource>().Select(s => s.Name).Should().BeEquivalentTo("roslyn", "workspace", "sandbox");
        }

        foreach (var local in (RunScopedMcpDefinition[])[
            new() { Remote = true, Args = ["x"] },
            new() { Remote = true, Env = new Dictionary<string, string>(StringComparer.Ordinal) },
            new() { Remote = true, Cwd = "x" },
            new() { Remote = true, ReadyTool = "x" },
            new() { Remote = true, Reload = "restart" },
            new() { Remote = true, ReadyWaitTimeout = TimeSpan.FromMinutes(5) },
        ])
        {
            var act = () => new ServiceCollection().AddThalos(t => t.AddMcpServer("roslyn", HostDefinition(local)));
            act.Should().Throw<ArgumentException>().WithMessage("*is remote*");
        }

        var twice = () => new ServiceCollection().AddThalos(t => t
            .AddMcpServer("roslyn", HostDefinition(new RunScopedMcpDefinition { Remote = true }))
            .AddRemoteRunTools("roslyn", typeof(WorkspaceTools)));
        twice.Should().Throw<ArgumentException>().WithMessage("A remote run tool source named 'roslyn' was already added.*");
        var remoteTwice = () => new ServiceCollection().AddThalos(t => t
            .AddRemoteRunTools("workspace", typeof(WorkspaceTools))
            .AddRemoteRunTools("workspace", typeof(WorkspaceTools)));
        remoteTwice.Should().Throw<ArgumentException>().WithMessage("A remote run tool source named 'workspace' was already added.*");
    }

    /// <summary>
    /// Ruling R32. Red 1: in ThalosBuilder.AddLocalTools, drop the ToolSourceNames.AddLocal call; a local source after a
    /// remote one is then accepted. Red 2: in ToolSourceNames.AddRemote, drop the local-name check; a remote source after
    /// a local one is then accepted. Red 3: in McpThalosBuilderExtensions.AddRemote, drop the McpNames check; a remote
    /// source after a plain MCP entry is then accepted. Red 4: in AddRemote, do not record the name in McpNames; a plain
    /// MCP entry after a remote source is then accepted. Red 5: in AddMcpServer, drop the ToolSourceNames.AddRemote call
    /// for a remote entry; a local source after it, and a remote entry after a local source, are then accepted.
    /// </summary>
    [Fact]
    public void A_remote_source_never_shares_its_name_with_a_local_or_MCP_source_in_either_order()
    {
        var any = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" };
        (string Case, Action<ThalosBuilder> Configure)[] clashes =
        [
            ("local workspace, then remote", t => t.UseRunWorkspaceTools(any).AddRemoteRunTools("workspace", typeof(WorkspaceTools))),
            ("remote workspace, then local", t => t.AddRemoteRunTools("workspace", typeof(WorkspaceTools)).UseRunWorkspaceTools(any)),
            ("local roslyn, then remote MCP entry", t => t.AddLocalTools("roslyn", typeof(WorkspaceTools)).AddMcpServer("roslyn", HostDefinition(new RunScopedMcpDefinition { Remote = true }))),
            ("remote MCP entry, then local roslyn", t => t.AddMcpServer("roslyn", HostDefinition(new RunScopedMcpDefinition { Remote = true })).AddLocalTools("roslyn", typeof(WorkspaceTools))),
            ("plain MCP entry, then remote tools", t => t.AddMcpServer("roslyn", PlainDefinition()).AddRemoteRunTools("roslyn", typeof(WorkspaceTools))),
            ("remote tools, then plain MCP entry", t => t.AddRemoteRunTools("roslyn", typeof(WorkspaceTools)).AddMcpServer("roslyn", PlainDefinition())),
            ("plain MCP entry, then remote MCP entry", t => t.AddMcpServer("roslyn", PlainDefinition()).AddMcpServer("roslyn", HostDefinition(new RunScopedMcpDefinition { Remote = true }))),
            ("remote MCP entry, then plain MCP entry", t => t.AddMcpServer("roslyn", HostDefinition(new RunScopedMcpDefinition { Remote = true })).AddMcpServer("roslyn", PlainDefinition())),
        ];

        using var _ = new AssertionScope();
        foreach (var (name, configure) in clashes)
        {
            var act = () => new ServiceCollection().AddThalos(configure);
            act.Should().Throw<ArgumentException>(name).WithMessage("*already added*");
        }
    }

    // ---------- helpers ----------

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }

        foreach (var remote in _remotes)
        {
            remote.Dispose();
        }

        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // a stopped stdio server may still hold a file for a moment; the temp directory is left behind
        }
        catch (UnauthorizedAccessException)
        {
            // git marks its objects read-only on Windows; the temp directory is left behind
        }
    }

    private string LogText => string.Join('\n', _logLines);

    private static McpServerDefinition PlainDefinition() => HostDefinition(null);

    private static RunWorkspace Workspace(LoopbackSandbox sandbox) => new(sandbox.RunId, "repo", "remote", "main", "run/feature", "sandbox://a", "App.slnx");

    private static McpServerDefinition HostDefinition(RunScopedMcpDefinition? runScoped) => new()
    {
        Type = "stdio",
        Command = "dotnet",
        Args = [LoopbackSandbox.ServerDll, "--host"],
        ShutdownTimeout = TimeSpan.FromSeconds(1),
        RunScoped = runScoped,
    };

    /// <summary>Asserts that <paramref name="source"/>'s local schemas equal what the sandbox's <c>/mcp/{source}</c> lists.</summary>
    private static async Task AssertSchemasMatchAsync(IServiceProvider sp, LoopbackSandbox sandbox, string source)
    {
        var local = (await Source(sp, source).GetToolsAsync(CancellationToken.None)).Value.Cast<AIFunction>().ToDictionary(f => f.Name, StringComparer.Ordinal);
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = sandbox.Endpoint(source).Endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>(StringComparer.Ordinal) { ["Authorization"] = $"Bearer {sandbox.Token}" },
                EnableStandaloneGetStream = false,
            },
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance));
        var listed = await client.ListToolsAsync();
        var names = listed.Select(t => t.Name).ToList();

        using var _ = new AssertionScope();
        local.Should().NotBeEmpty();
        local.Keys.Should().BeEquivalentTo(names, source);
        foreach (var tool in listed)
        {
            if (!local.TryGetValue(tool.Name, out var schema))
            {
                continue;
            }

            schema.Description.Should().Be(tool.Description, $"{source}__{tool.Name}'s description");
            JsonElement.DeepEquals(schema.JsonSchema, tool.ProtocolTool.InputSchema).Should().BeTrue(
                $"{source}__{tool.Name}'s schema: local {schema.JsonSchema} vs sandbox {tool.ProtocolTool.InputSchema}");
        }
    }

    private ServiceProvider Services(
        RemoteRunToolOptions? options = null,
        IRunToolCallObserver[]? observers = null,
        IRunWorkspaceProvider? provider = null,
        RunScopedMcpDefinition? roslyn = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(_ => LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(new CapturingLoggers(_logLines)))); // owned by the container
        services.AddSingleton<IRunToolEndpointResolver>(_resolver);
        if (options is not null)
        {
            services.AddSingleton(options);
        }

        if (provider is not null)
        {
            services.AddSingleton(provider);
        }

        foreach (var observer in observers ?? [])
        {
            services.AddSingleton(observer);
        }

        services.AddThalos(t => t
            .AddRemoteRunTools(RunWorkspaceToolOptions.SourceName, typeof(WorkspaceTools))
            .AddRemoteRunTools(SandboxToolOptions.SourceName, typeof(SandboxTools))
            .AddMcpServer("roslyn", HostDefinition(roslyn ?? new RunScopedMcpDefinition { Remote = true })));
        return services.BuildServiceProvider();
    }

    private static RemoteRunToolSource Source(IServiceProvider sp, string name) =>
        sp.GetServices<IToolSource>().OfType<RemoteRunToolSource>().Single(s => string.Equals(s.Name, name, StringComparison.Ordinal));

    private static async Task<Dictionary<string, AIFunction>> SchemaFunctionsAsync(IServiceProvider sp, string name)
    {
        var schemas = Source(sp, name).SchemaSource!;
        return (await schemas.GetToolsAsync(CancellationToken.None)).Value.Cast<AIFunction>().ToDictionary(f => f.Name, StringComparer.Ordinal);
    }

    private static async Task<string> CallAsync(IServiceProvider sp, string source, string tool, params (string Name, object? Value)[] arguments)
    {
        var tools = await Source(sp, source).GetToolsAsync(CancellationToken.None);
        tools.IsSuccess.Should().BeTrue(tools.IsFailure ? tools.Error.Message : "");
        var function = tools.Value.Cast<AIFunction>().Single(f => string.Equals(f.Name, tool, StringComparison.Ordinal));
        return (await function.InvokeAsync(Arguments(arguments), CancellationToken.None))?.ToString() ?? "";
    }

    private static async Task<string> CallAsAsync(IServiceProvider sp, Guid runId, string source, string tool, params (string Name, object? Value)[] arguments)
    {
        using var _ = BeginTurn(RunCaller(runId));
        return await CallAsync(sp, source, tool, arguments);
    }

    private static AIFunctionArguments Arguments(params (string Name, object? Value)[] arguments)
    {
        var result = new AIFunctionArguments(StringComparer.Ordinal);
        foreach (var (name, value) in arguments)
        {
            result[name] = value;
        }

        return result;
    }

    private static TurnScope BeginTurn(ISecurityContext caller) => TurnScope.Begin(SessionId.New(), TurnId.New(), caller);

    private static TestCaller RunCaller(Guid runId) =>
        new($"run:{runId:D}", new Dictionary<string, string>(StringComparer.Ordinal) { [RunWorkspaceClaims.RunId] = runId.ToString("D") });

    /// <summary>A sandbox under <c>_temp/name</c>, mapped by the resolver to its run.</summary>
    private async Task<LoopbackSandbox> SandboxAsync(string name)
    {
        var sandbox = await LoopbackSandbox.StartAsync(Path.Combine(_temp, name));
        _disposables.Add(sandbox);
        _resolver.Map[sandbox.RunId] = source => sandbox.Endpoint(source);
        return sandbox;
    }

    /// <summary>A sandbox that imported a repository holding <c>Marker_name.cs</c>, with Roslyn ready.</summary>
    private async Task<LoopbackSandbox> ImportedSandboxAsync(string name)
    {
        var sandbox = await SandboxAsync(name);
        var remote = LocalGitRemote.Create(("App.slnx", "<Solution />\n"), ($"Marker_{name}.cs", "class M { }\n"));
        _remotes.Add(remote);
        await sandbox.ImportAsync(remote, Path.Combine(_temp, name + "-mirror"));
        return sandbox;
    }

    /// <summary>Answers as the real sandbox host does, one server-sent event carrying the JSON-RPC message.</summary>
    private static async Task WriteEventAsync(HttpResponse response, string message)
    {
        response.ContentType = "text/event-stream";
        await response.WriteAsync($"event: message\ndata: {message}\n\n");
    }

    private sealed class EmptySchemas : IToolSource
    {
        public string Name => "x";

        public ValueTask<Result<IReadOnlyList<AITool>, AgentError>> GetToolsAsync(CancellationToken ct) =>
            ValueTask.FromResult(Result<IReadOnlyList<AITool>, AgentError>.Success([]));
    }

    private sealed class FakeResolver : IRunToolEndpointResolver
    {
        public ConcurrentDictionary<Guid, Func<string, RunToolEndpoint>> Map { get; } = new();

        public ConcurrentQueue<(Guid RunId, string Source)> ResolvedQueue { get; } = new();

        public IReadOnlyList<(Guid RunId, string Source)> Resolved => [.. ResolvedQueue];

        /// <summary>Run before each answer, as a removal racing the resolve would.</summary>
        public Func<Guid, ValueTask>? BeforeAnswer { get; set; }

        public async ValueTask<RunToolEndpoint?> ResolveAsync(Guid runId, string source, CancellationToken ct)
        {
            ResolvedQueue.Enqueue((runId, source));
            var endpoint = Map.TryGetValue(runId, out var map) ? map(source) : null;
            if (BeforeAnswer is { } before)
            {
                await before(runId);
            }

            return endpoint;
        }
    }

    private sealed class OneWorkspace(RunWorkspace workspace) : IRunWorkspaceProvider
    {
        public ValueTask<Result<RunWorkspace, AgentError>> CreateAsync(RunWorkspaceRequest request, CancellationToken ct) =>
            ValueTask.FromResult(Result<RunWorkspace, AgentError>.Success(workspace));

        public ValueTask<RunWorkspace?> FindAsync(Guid runId, CancellationToken ct) =>
            ValueTask.FromResult(runId == workspace.RunId ? workspace : null);

        public ValueTask<IReadOnlyList<RunWorkspace>> ListAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<RunWorkspace>>([workspace]);

        public ValueTask<UnitResult<AgentError>> RemoveAsync(Guid runId, CancellationToken ct) => ValueTask.FromResult(UnitResult<AgentError>.Success());
    }

    private sealed class RecordingObserver : IRunToolCallObserver
    {
        public ConcurrentQueue<RunToolCall> Calls { get; } = new();

        public ValueTask OnCompletedAsync(RunToolCall completed, CancellationToken ct)
        {
            Calls.Enqueue(completed);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HangingObserver : IRunToolCallObserver
    {
        private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the token the observer was given is cancelled.</summary>
        public TaskCompletionSource TokenCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Never completes, and ignores its token beyond recording that it was cancelled.</summary>
        public ValueTask OnCompletedAsync(RunToolCall completed, CancellationToken ct)
        {
            ct.Register(() => TokenCancelled.TrySetResult());
            return new(_never.Task);
        }
    }

    private sealed class ThrowingObserver : IRunToolCallObserver
    {
        public ValueTask OnCompletedAsync(RunToolCall completed, CancellationToken ct) => throw new InvalidOperationException("observer boom");
    }

    /// <summary>Every log line, at every level, with its exception, from the sources and the MCP clients.</summary>
    private sealed class CapturingLoggers(ConcurrentQueue<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, lines);

        public void Dispose()
        {
            // nothing to release: the lines belong to the test
        }

        private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue($"{category} {logLevel}: {formatter(state, exception)} {exception}");
        }
    }
}

internal sealed class TestCaller(string id, IReadOnlyDictionary<string, string>? claims = null) : ISecurityContext
{
    public string Id { get; } = id;

    public IReadOnlySet<string> Roles { get; } = new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Claims { get; } = claims ?? new Dictionary<string, string>(StringComparer.Ordinal);
}
