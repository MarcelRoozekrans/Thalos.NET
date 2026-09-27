using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using AwesomeAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Thalos.Mcp;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Thalos.Tests.Mcp;

public sealed class RunMcpServerRegistryTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("thalos-run-mcp-").FullName;
    private readonly List<RunMcpServerRegistry> _registries = [];

    private Guid RunId { get; } = Guid.NewGuid();

    private static string[] ServerArgs => [McpServerFixture.ServerDll];

    [Fact]
    public async Task A_run_server_starts_with_the_workspace_substituted_into_its_args()
    {
        var registry = Registry(runScoped: new() { Args = [.. ServerArgs, "--solution", "${run.workspace.solution}"] });
        await registry.OnReadyAsync(Workspace(solution: "C:/w/run1/App.sln"), CancellationToken.None);

        (await registry.WaitAllReadyAsync(RunId, TimeSpan.FromSeconds(30), CancellationToken.None)).IsSuccess.Should().BeTrue();
        await using var lease = await LeaseAsync(registry);
        (await CallAsync(lease.Client, "args")).Should().Contain("C:/w/run1/App.sln");
    }

    [Fact]
    public async Task Readiness_times_out_naming_the_server_when_the_ready_tool_never_succeeds_in_time()
    {
        var registry = Registry(runScoped: new() { Args = [.. ServerArgs, "--ready-after", "60000"], ReadyTool = "ready_after" });
        await registry.OnReadyAsync(Workspace(), CancellationToken.None);
        var waited = await registry.WaitAllReadyAsync(RunId, TimeSpan.FromSeconds(3), CancellationToken.None);
        waited.IsFailure.Should().BeTrue();
        waited.Error.Message.Should().Contain("roslyn").And.Contain("not ready within");
    }

    [Fact]
    public async Task Readiness_waits_until_the_ready_tool_succeeds()
    {
        var registry = Registry(runScoped: new() { Args = [.. ServerArgs, "--ready-after", "5000"], ReadyTool = "ready_after" });
        await registry.OnReadyAsync(Workspace(), CancellationToken.None);

        var waited = await registry.WaitAllReadyAsync(RunId, TimeSpan.FromSeconds(30), CancellationToken.None);
        waited.IsSuccess.Should().BeTrue(waited.IsFailure ? waited.Error.Message : "");
        await using var lease = await LeaseAsync(registry);
        (await lease.Client.CallToolAsync("ready_after")).IsError.Should().NotBe(true, "readiness is reported only once the ready tool answers");
    }

    [Fact]
    public async Task A_tool_reload_runs_once_before_the_next_call_after_a_change()
    {
        var registry = Registry(runScoped: new() { Args = ServerArgs, Reload = "tool:reload_count" });
        await registry.OnReadyAsync(Workspace(), CancellationToken.None);
        registry.OnFilesChanged(RunId, ["a.cs"]);
        await using (var lease = await LeaseAsync(registry))
        {
            (await CallAsync(lease.Client, "reload_count")).Should().Be("2", "one reload plus this call");
        }

        await using (var lease = await LeaseAsync(registry)) // not dirty any more
        {
            (await CallAsync(lease.Client, "reload_count")).Should().Be("3");
        }
    }

    [Fact]
    public async Task A_restart_reload_gives_a_new_server_process()
    {
        var registry = Registry(runScoped: new() { Args = ServerArgs, Reload = "restart" });
        await registry.OnReadyAsync(Workspace(), CancellationToken.None);
        var before = await PidAsync(registry);
        registry.OnFilesChanged(RunId, ["a.cs"]);
        (await PidAsync(registry)).Should().NotBe(before);
        IsRunning(before).Should().BeFalse("the replaced server's process is shut down, not leaked");
    }

    [Fact]
    public async Task A_removed_run_has_no_server_and_is_never_served_by_another()
    {
        var registry = Registry(runScoped: new() { Args = ServerArgs });
        await registry.OnReadyAsync(Workspace(), CancellationToken.None);
        var pid = await PidAsync(registry);
        await registry.OnRemovingAsync(Workspace(), CancellationToken.None);
        (await registry.GetReadyClientAsync("roslyn", RunId, CancellationToken.None)).IsFailure.Should().BeTrue();
        IsRunning(pid).Should().BeFalse("the removal waits until the run's server process is gone");
    }

    [Fact]
    public async Task After_a_restart_a_waiting_run_starts_its_server_from_the_workspace_record()
    {
        var registry = Registry(runScoped: new() { Args = ServerArgs }, workspaces: ProviderThatFinds(Workspace()));
        (await registry.WaitAllReadyAsync(RunId, TimeSpan.FromSeconds(30), CancellationToken.None)).IsSuccess.Should().BeTrue("OnReadyAsync never ran in this process");
    }

    [Fact]
    public async Task Without_a_workspace_provider_a_run_is_refused_never_served()
    {
        var registry = Registry(runScoped: new() { Args = ServerArgs }, workspaces: null);
        await registry.OnReadyAsync(Workspace(), CancellationToken.None); // an observer may still be told of a workspace; nothing may start
        var waited = await registry.WaitAllReadyAsync(RunId, TimeSpan.FromSeconds(5), CancellationToken.None);
        waited.IsFailure.Should().BeTrue();
        waited.Error.Message.Should().Contain("no run workspace provider");
        (await registry.GetReadyClientAsync("roslyn", RunId, CancellationToken.None)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task OnReadyAsync_returns_before_a_slow_server_is_ready_and_never_calls_back_into_the_provider()
    {
        var provider = ProviderThatFinds(null);
        var registry = Registry(runScoped: new() { Args = [.. ServerArgs, "--ready-after", "15000"], ReadyTool = "ready_after" }, provider);

        var sw = Stopwatch.StartNew();
        await registry.OnReadyAsync(Workspace(), CancellationToken.None);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "the provider calls OnReadyAsync under its repository lock");
        provider.Calls.Should().Be(0, "OnReadyAsync runs under the provider's lock and must call none of its methods");

        var waited = await registry.WaitAllReadyAsync(RunId, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        waited.IsFailure.Should().BeTrue("the server was still starting when OnReadyAsync returned");

        var beforeRemoving = provider.Calls;
        await registry.OnRemovingAsync(Workspace(), CancellationToken.None);
        provider.Calls.Should().Be(beforeRemoving, "OnRemovingAsync runs under the provider's lock and must call none of its methods");
    }

    [Fact]
    public async Task Run_placeholders_are_substituted_into_args_env_and_the_default_cwd_and_others_are_kept()
    {
        var registry = Registry(
            runScoped: new()
            {
                Args = [.. ServerArgs, "--id", "${run.id}", "--root", "${run.workspace.root}", "--keep", "${other.thing}"],
                Env = new Dictionary<string, string>(StringComparer.Ordinal) { ["THALOS_RUN_ENV"] = "id=${run.id}" },
            },
            new FakeProvider(null),
            hostEnv: new Dictionary<string, string>(StringComparer.Ordinal) { ["THALOS_HOST_ONLY"] = "host", ["THALOS_RUN_ENV"] = "host-value" });
        await registry.OnReadyAsync(Workspace(), CancellationToken.None);
        await using var lease = await LeaseAsync(registry);
        var client = lease.Client;

        var args = await CallAsync(client, "args");
        args.Should().Contain($"--id {RunId:D}");
        args.Should().Contain($"--root {_root}");
        args.Should().Contain("--keep ${other.thing}", "an unknown placeholder is left verbatim");
        (await CallAsync(client, "env", new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = "THALOS_RUN_ENV" })).Should().Be($"id={RunId:D}", "runScoped.env wins over the host entry's env");
        (await CallAsync(client, "env", new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = "THALOS_HOST_ONLY" })).Should().Be("host", "the host entry's env is kept underneath");
        Normalize(await CallAsync(client, "cwd")).Should().Be(Normalize(_root), "the default cwd is the workspace root");
    }

    [Fact]
    public async Task A_percent_sign_in_a_substituted_value_fails_the_start_on_Windows_where_cmd_would_expand_it()
    {
        var root = Directory.CreateDirectory(Path.Combine(_root, "a%PATH%b")).FullName;
        var registry = Registry(runScoped: new() { Args = [.. ServerArgs, "--root", "${run.workspace.root}"], Cwd = _root });
        await registry.OnReadyAsync(Workspace() with { Root = root }, CancellationToken.None);

        var waited = await registry.WaitAllReadyAsync(RunId, TimeSpan.FromSeconds(30), CancellationToken.None);
        if (OperatingSystem.IsWindows())
        {
            waited.IsFailure.Should().BeTrue("the MCP SDK starts the server through cmd.exe, which would expand %PATH%");
            waited.Error.Message.Should().Contain("contains '%'");
        }
        else
        {
            waited.IsSuccess.Should().BeTrue("no shell parses the arguments off Windows");
            await using var lease = await LeaseAsync(registry);
            (await CallAsync(lease.Client, "args")).Should().Contain($"--root {root}", "the value reaches the server unchanged");
        }
    }

    [Fact]
    public async Task A_solution_placeholder_for_a_workspace_without_a_solution_fails_the_start()
    {
        var registry = Registry(runScoped: new() { Args = [.. ServerArgs, "--solution", "${run.workspace.solution}"] });
        await registry.OnReadyAsync(Workspace(solution: null), CancellationToken.None);

        var waited = await registry.WaitAllReadyAsync(RunId, TimeSpan.FromSeconds(30), CancellationToken.None);
        waited.IsFailure.Should().BeTrue();
        waited.Error.Message.Should().Contain("run workspace has no solution");
    }

    [Theory]
    [InlineData("no_such_tool", "none", "ready tool 'no_such_tool'")]
    [InlineData(null, "tool:no_such_tool", "reload tool 'no_such_tool'")]
    public async Task A_ready_or_reload_tool_the_server_does_not_offer_fails_the_start(string? readyTool, string reload, string expected)
    {
        var registry = Registry(runScoped: new() { Args = ServerArgs, ReadyTool = readyTool, Reload = reload });
        await registry.OnReadyAsync(Workspace(), CancellationToken.None);

        var waited = await registry.WaitAllReadyAsync(RunId, TimeSpan.FromSeconds(10), CancellationToken.None);
        waited.IsFailure.Should().BeTrue();
        waited.Error.Message.Should().Contain(expected);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("tool:")]
    [InlineData("Restart")]
    public void An_unknown_reload_is_rejected_at_construction(string reload)
    {
        var act = () => Registry(runScoped: new() { Args = ServerArgs, Reload = reload });
        act.Should().Throw<ArgumentException>().WithMessage($"*reload '{reload}'*");
    }

    [Theory]
    [InlineData(0, 60, "readyWaitTimeout")]
    [InlineData(-1, 60, "readyWaitTimeout")]
    [InlineData(60, 0, "callTimeout")]
    [InlineData(60, 2_147_484, "callTimeout")] // past int.MaxValue milliseconds, the longest a timer takes
    public void A_timeout_that_is_not_positive_or_too_long_for_a_timer_is_rejected_at_construction(int readySeconds, int callSeconds, string property)
    {
        var act = () => Registry(runScoped: new() { Args = ServerArgs, ReadyWaitTimeout = TimeSpan.FromSeconds(readySeconds), CallTimeout = TimeSpan.FromSeconds(callSeconds) });
        act.Should().Throw<ArgumentException>().WithMessage($"*{property}*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)] // Timeout.InfiniteTimeSpan
    [InlineData(2_147_483_648)] // past int.MaxValue milliseconds, the longest a timer takes
    public void A_shutdown_timeout_that_is_not_positive_or_too_long_for_a_timer_is_rejected_at_construction(long milliseconds)
    {
        var definition = McpServerFixture.Definition("--host");
        definition.ShutdownTimeout = TimeSpan.FromMilliseconds(milliseconds);
        definition.RunScoped = new() { Args = ServerArgs };
        var act = () => new RunMcpServerRegistry(Servers(definition), () => null, NullLoggerFactory.Instance, TimeProvider.System);
        act.Should().Throw<ArgumentException>().WithParameterName("servers").WithMessage("*shutdownTimeout*");
    }

    [Fact]
    public void The_workspace_provider_is_not_looked_up_while_the_registry_is_built()
    {
        var lookedUp = false;
        var definition = McpServerFixture.Definition();
        definition.RunScoped = new() { Args = ServerArgs };
        var registry = new RunMcpServerRegistry(Servers(definition), () => { lookedUp = true; return null; }, NullLoggerFactory.Instance, TimeProvider.System);
        _registries.Add(registry);

        lookedUp.Should().BeFalse("a container builds the registry while it builds the provider that observes it");
    }

    [Fact]
    public void A_definition_without_runScoped_or_that_is_not_stdio_is_rejected_at_construction()
    {
        var noRunScoped = () => new RunMcpServerRegistry(Servers(McpServerFixture.Definition()), () => new FakeProvider(null), NullLoggerFactory.Instance, TimeProvider.System);
        var http = () => new RunMcpServerRegistry(
            Servers(new McpServerDefinition { Type = "http", Url = "http://localhost:1", RunScoped = new() }), () => new FakeProvider(null), NullLoggerFactory.Instance, TimeProvider.System);
        noRunScoped.Should().Throw<ArgumentException>().WithMessage("*no runScoped*");
        http.Should().Throw<ArgumentException>().WithMessage("*must be a stdio server*");
    }

    [Fact]
    public async Task With_no_run_scoped_server_configured_readiness_succeeds_at_once_even_without_a_provider()
    {
        var registry = new RunMcpServerRegistry(new Dictionary<string, McpServerDefinition>(StringComparer.Ordinal), workspaces: () => null, NullLoggerFactory.Instance, TimeProvider.System);
        _registries.Add(registry);
        (await registry.WaitAllReadyAsync(RunId, TimeSpan.Zero, CancellationToken.None)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_change_reported_while_a_reload_is_running_is_reloaded_again_on_the_next_call()
    {
        var log = Path.Combine(_root, "calls.log");
        var registry = Registry(runScoped: new() { Args = [.. ServerArgs, "--reload-delay-ms", "1500", "--call-log", log], Reload = "tool:reload_count" });
        await registry.OnReadyAsync(Workspace(), CancellationToken.None);
        (await registry.WaitAllReadyAsync(RunId, TimeSpan.FromSeconds(30), CancellationToken.None)).IsSuccess.Should().BeTrue();

        registry.OnFilesChanged(RunId, ["a.cs"]);
        var first = Task.Run(async () => await registry.GetReadyClientAsync("roslyn", RunId, CancellationToken.None));
        await UntilAsync(() => File.Exists(log), "the first reload to begin");
        await Task.Run(() => registry.OnFilesChanged(RunId, ["b.cs"])); // lands while that reload is still running
        var firstLease = await first;
        firstLease.IsSuccess.Should().BeTrue();
        await firstLease.Value.DisposeAsync();

        await using var lease = await LeaseAsync(registry);
        (await CallAsync(lease.Client, "reload_count")).Should().Be("3", "two reloads, the second for the change made during the first, plus this call");
    }

    [Fact]
    public async Task Parallel_callers_after_a_change_share_one_restarted_server()
    {
        var registry = Registry(runScoped: new() { Args = ServerArgs, Reload = "restart" });
        await registry.OnReadyAsync(Workspace(), CancellationToken.None);
        var before = await PidAsync(registry);

        registry.OnFilesChanged(RunId, ["a.cs"]);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () => await registry.GetReadyClientAsync("roslyn", RunId, CancellationToken.None))));
        try
        {
            var client = results.Select(r => r.IsSuccess ? r.Value.Client : null).Distinct().Should().ContainSingle("one restart serves every caller waiting behind it").Which;
            client.Should().NotBeNull("that restart succeeded");
            int.Parse(await CallAsync(client!, "pid"), CultureInfo.InvariantCulture).Should().NotBe(before);
            IsRunning(before).Should().BeFalse();
        }
        finally
        {
            foreach (var result in results)
            {
                if (result.IsSuccess)
                {
                    await result.Value.DisposeAsync();
                }
            }
        }
    }

    [Fact]
    public async Task A_restart_waits_for_a_call_in_flight_instead_of_cutting_it_off()
    {
        var log = Path.Combine(_root, "calls.log");
        var registry = Registry(runScoped: new() { Args = [.. ServerArgs, "--call-log", log], Reload = "restart" });
        await registry.OnReadyAsync(Workspace(), CancellationToken.None);
        var first = await LeaseAsync(registry);
        var before = int.Parse(await CallAsync(first.Client, "pid"), CultureInfo.InvariantCulture);

        var inFlight = Task.Run(async () =>
        {
            await using (first)
            {
                return await first.Client.CallToolAsync("slow", new Dictionary<string, object?>(StringComparer.Ordinal) { ["ms"] = 1500 });
            }
        });
        await UntilAsync(() => CallLog.Read(log).Contains("slow", StringComparison.Ordinal), "the slow call to begin");
        registry.OnFilesChanged(RunId, ["a.cs"]);
        await using var second = await Task.Run(async () => await LeaseAsync(registry));

        var call = async () => await inFlight;
        await call.Should().NotThrowAsync("the restart waited for the call's lease instead of closing its transport");
        int.Parse(await CallAsync(second.Client, "pid"), CultureInfo.InvariantCulture).Should().NotBe(before, "the restart still happened, after the call");
    }

    [Fact]
    public async Task A_tool_reload_waits_for_a_call_in_flight_and_never_overlaps_it()
    {
        var log = Path.Combine(_root, "calls.log");
        var registry = Registry(runScoped: new() { Args = [.. ServerArgs, "--call-log", log, "--reload-delay-ms", "1000"], Reload = "tool:reload_count" });
        await registry.OnReadyAsync(Workspace(), CancellationToken.None);
        var first = await LeaseAsync(registry);

        var inFlight = Task.Run(async () =>
        {
            await using (first)
            {
                return await first.Client.CallToolAsync("slow", new Dictionary<string, object?>(StringComparer.Ordinal) { ["ms"] = 1500 });
            }
        });
        await UntilAsync(() => CallLog.Read(log).Contains("slow", StringComparison.Ordinal), "the slow call to begin");
        registry.OnFilesChanged(RunId, ["a.cs"]);
        await using var second = await Task.Run(async () => await LeaseAsync(registry));

        await inFlight;
        (await CallAsync(second.Client, "overlaps")).Should().Be("0", "the reload waited until the call's lease was released");
        CallLog.Lines(log).Should().Equal(["slow", "reload_count"], "the reload still ran, after the call");
    }

    [Fact]
    public async Task Removing_a_run_cancels_a_reload_waiting_for_a_held_lease_and_stops_the_server_anyway()
    {
        var registry = Registry(runScoped: new() { Args = ServerArgs, Reload = "tool:reload_count" });
        await registry.OnReadyAsync(Workspace(), CancellationToken.None);
        await using var held = await LeaseAsync(registry);
        var pid = int.Parse(await CallAsync(held.Client, "pid"), CultureInfo.InvariantCulture);

        registry.OnFilesChanged(RunId, ["a.cs"]);
        var waiting = Task.Run(async () => await registry.GetReadyClientAsync("roslyn", RunId, CancellationToken.None));
        await UntilAsync(() => registry.ReloadsWaitingForLeases == 1, "the reload to wait for the held lease");

        var removal = registry.OnRemovingAsync(Workspace(), CancellationToken.None).AsTask();
        (await Task.WhenAny(removal, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(removal, "removal does not wait for a held lease");
        (await waiting).IsFailure.Should().BeTrue("the waiting reload was cancelled, not served");
        IsRunning(pid).Should().BeFalse("the server is stopped even though a lease is still held");
    }

    [Fact]
    public async Task A_waiter_that_gives_up_leaves_the_reload_running_and_no_lease_is_handed_out_before_it_finishes()
    {
        var registry = Registry(runScoped: new() { Args = ServerArgs, Reload = "tool:reload_count" });
        await registry.OnReadyAsync(Workspace(), CancellationToken.None);
        var held = await LeaseAsync(registry);

        registry.OnFilesChanged(RunId, ["a.cs"]);
        using (var impatient = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
        {
            var giveUp = async () => await registry.GetReadyClientAsync("roslyn", RunId, impatient.Token);
            await giveUp.Should().ThrowAsync<OperationCanceledException>("the waiter's own token ends its wait");
        }

        var stillWaiting = registry.ReloadsWaitingForLeases;
        var next = Task.Run(async () => await registry.GetReadyClientAsync("roslyn", RunId, CancellationToken.None));
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        var handedOutBeforeRelease = next.IsCompleted;
        await held.DisposeAsync();
        var lease = await next.WaitAsync(TimeSpan.FromSeconds(10));
        var count = lease.IsFailure ? $"refused: {lease.Error.Message}" : null;
        if (lease.IsSuccess)
        {
            await using (lease.Value)
            {
                count = await CallAsync(lease.Value.Client, "reload_count");
            }
        }

        using var _scope = new AssertionScope();
        stillWaiting.Should().Be(1, "the reload belongs to the server; the waiter that began it giving up does not cancel it");
        handedOutBeforeRelease.Should().BeFalse("no lease is handed out while the reload waits for the held one");
        count.Should().Be("2", "the one reload ran once the held lease was released, plus this call");
    }

    [Fact]
    public async Task Removing_a_run_while_a_restart_reload_replaces_its_server_waits_for_the_reload_and_leaves_no_process()
    {
        var pidFile = Path.Combine(_root, "server.pid");
        var workspace = Workspace() with { Root = Directory.CreateDirectory(Path.Combine(_root, "restart-ws")).FullName };
        var definition = McpServerFixture.Definition("--host");
        definition.ShutdownTimeout = TimeSpan.FromSeconds(3); // disposing the old server waits this long, then kills its process tree
        definition.RunScoped = new() { Args = [.. ServerArgs, "--pid-file", pidFile], Reload = "restart" };
        var events = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(events));
        var registry = new RunMcpServerRegistry(Servers(definition), () => new FakeProvider(null), loggerFactory, TimeProvider.System);
        _registries.Add(registry);
        await registry.OnReadyAsync(workspace, CancellationToken.None);
        var oldPid = await PidAsync(registry);
        var startsBefore = events.Count(StartingEvent);

        registry.OnFilesChanged(RunId, ["a.cs"]);
        using (var impatient = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
        {
            // Begins the restart, then gives up while the restart is still disposing the old server.
            var begin = async () => await registry.GetReadyClientAsync("roslyn", RunId, impatient.Token);
            await begin.Should().ThrowAsync<OperationCanceledException>();
        }

        var removal = async () => await registry.OnRemovingAsync(workspace, CancellationToken.None);
        await removal.Should().NotThrowAsync("a removal during a restart finishes, whatever the restart is doing");
        var replacementStartsWhenRemoved = events.Count(StartingEvent) - startsBefore;
        var replacementSkippedWhenRemoved = events.Count(StartSkippedEvent);
        var oldAliveAfterRemoval = IsRunning(oldPid);
        var workspaceFreeAtRemoval = TryDelete(workspace.Root); // on Windows, fails while any process has it as its working directory
        await Task.Delay(TimeSpan.FromSeconds(4)); // long enough for a restart the removal did not wait for to have started its process
        var lastPid = await PidFromFileAsync(pidFile);
        string after;
        try
        {
            var result = await registry.GetReadyClientAsync("roslyn", RunId, CancellationToken.None);
            after = result.IsFailure ? $"refused: {result.Error.Message}" : "served";
        }
        catch (Exception ex)
        {
            after = $"threw {ex.GetType().Name}: {ex.Message}";
        }

        using var _scope = new AssertionScope();
        replacementSkippedWhenRemoved.Should().Be(1, "the removal waited for the restart, which then found the run stopping and started nothing");
        replacementStartsWhenRemoved.Should().Be(0, "no replacement process is started in a workspace being removed");
        oldAliveAfterRemoval.Should().BeFalse("the removal waits for the in-flight restart, which is still shutting the old process down");
        workspaceFreeAtRemoval.Should().BeTrue("once the removal returns, no process has the run's workspace as its working directory");
        lastPid.Should().Be(oldPid, "no replacement process was started, so none wrote its pid");
        IsRunning(lastPid).Should().BeFalse("no process outlives the removal");
        after.Should().StartWith("refused:", "a call after the removal gets a failure result, not an exception");
    }

    private static readonly EventId StartingEvent = new(310);

    private static readonly EventId StartSkippedEvent = new(320);

    [Fact]
    public async Task Disposing_a_lease_twice_releases_it_once()
    {
        var registry = Registry(runScoped: new() { Args = ServerArgs, Reload = "tool:reload_count" });
        await registry.OnReadyAsync(Workspace(), CancellationToken.None);
        var once = await LeaseAsync(registry);
        await once.DisposeAsync();
        await once.DisposeAsync();
        var held = await LeaseAsync(registry);

        registry.OnFilesChanged(RunId, ["a.cs"]);
        var reload = Task.Run(async () => await registry.GetReadyClientAsync("roslyn", RunId, CancellationToken.None));
        await UntilAsync(() => registry.ReloadsWaitingForLeases == 1, "the reload to wait for the held lease, which a second dispose of the other lease must not have released");

        await held.DisposeAsync();
        var reloaded = await reload.WaitAsync(TimeSpan.FromSeconds(10));
        reloaded.IsSuccess.Should().BeTrue();
        await reloaded.Value.DisposeAsync();
    }

    [Fact]
    public async Task A_failed_tool_reload_is_retried_on_the_next_call()
    {
        var log = Path.Combine(_root, "calls.log");
        var ready = Path.Combine(_root, "ready");
        var registry = Registry(runScoped: new() { Args = [.. ServerArgs, "--ready-when", ready, "--call-log", log], Reload = "tool:ready_after" });
        await registry.OnReadyAsync(Workspace(), CancellationToken.None);
        (await registry.WaitAllReadyAsync(RunId, TimeSpan.FromSeconds(30), CancellationToken.None)).IsSuccess.Should().BeTrue();

        registry.OnFilesChanged(RunId, ["a.cs"]);
        var failed = await registry.GetReadyClientAsync("roslyn", RunId, CancellationToken.None);
        failed.IsFailure.Should().BeTrue("the reload tool answered with an error");
        failed.Error.Message.Should().Contain("could not reload");

        await File.WriteAllTextAsync(ready, ""); // the reload tool succeeds from now on
        await (await LeaseAsync(registry)).DisposeAsync();
        CallLog.Lines(log).Should().Equal(["ready_after", "ready_after"], "the failed reload is still pending and runs again");
    }

    [Fact]
    public async Task Readiness_starts_a_server_again_after_its_start_failed()
    {
        var registry = Registry(runScoped: new() { Args = [.. ServerArgs, "--fail-first-start", Path.Combine(_root, "failed-once")] });
        await registry.OnReadyAsync(Workspace(), CancellationToken.None);
        (await registry.GetReadyClientAsync("roslyn", RunId, CancellationToken.None)).IsFailure.Should().BeTrue("the first process exits before speaking MCP");

        var waited = await registry.WaitAllReadyAsync(RunId, TimeSpan.FromSeconds(30), CancellationToken.None);
        waited.IsSuccess.Should().BeTrue(waited.IsFailure ? waited.Error.Message : "");
    }

    [Fact]
    public async Task A_lookup_in_flight_when_its_run_is_removed_starts_no_server_and_its_mark_goes_with_it()
    {
        var provider = ProviderThatFinds(Workspace());
        var registry = Registry(runScoped: new() { Args = ServerArgs }, workspaces: provider);
        provider.HoldFinds();

        var racing = Task.Run(async () => await registry.WaitAllReadyAsync(RunId, TimeSpan.FromSeconds(30), CancellationToken.None));
        await provider.FindStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await registry.OnRemovingAsync(Workspace(), CancellationToken.None); // the record still exists: the provider deletes it after this returns
        provider.ReleaseFinds();

        var waited = await racing;
        waited.IsFailure.Should().BeTrue("the lookup began before the removal");
        waited.Error.Message.Should().Contain("was removed");
        (await registry.GetReadyClientAsync("roslyn", RunId, CancellationToken.None)).IsFailure.Should().BeTrue("the racing lookup started no server");
        registry.InFlightLookupCount.Should().Be(0, "a removal mark lives only as long as the lookup it marks");

        (await registry.WaitAllReadyAsync(RunId, TimeSpan.FromSeconds(30), CancellationToken.None)).IsSuccess.Should().BeTrue("nothing about the removal is remembered once no marked lookup is in flight");
    }

    [Fact]
    public async Task A_repeated_removal_notice_is_a_no_op()
    {
        var registry = Registry(runScoped: new() { Args = ServerArgs });
        await registry.OnReadyAsync(Workspace(), CancellationToken.None);
        var pid = await PidAsync(registry);
        await registry.OnRemovingAsync(Workspace(), CancellationToken.None);

        var again = async () => await registry.OnRemovingAsync(Workspace(), CancellationToken.None);

        await again.Should().NotThrowAsync("the provider delivers OnRemovingAsync at least once, so a repeat must be harmless");
        IsRunning(pid).Should().BeFalse();
    }

    [Fact]
    public async Task A_lookup_that_finds_no_workspace_leaves_nothing_tracked()
    {
        var registry = Registry(runScoped: new() { Args = ServerArgs }, workspaces: ProviderThatFinds(null));

        (await registry.WaitAllReadyAsync(RunId, TimeSpan.FromSeconds(5), CancellationToken.None)).IsFailure.Should().BeTrue();
        registry.InFlightLookupCount.Should().Be(0, "a lookup that found nothing is un-tracked too, not only one that goes on to start servers");
    }

    [Fact]
    public async Task Removing_a_run_while_its_server_is_starting_stops_it_promptly_and_leaves_no_process()
    {
        var pidFile = Path.Combine(_root, "server.pid");
        var registry = Registry(runScoped: new() { Args = [.. ServerArgs, "--pid-file", pidFile, "--delay-ms", "15000"] });
        await registry.OnReadyAsync(Workspace(), CancellationToken.None);
        var pid = await PidFromFileAsync(pidFile);

        var sw = Stopwatch.StartNew();
        await Task.Run(async () => await registry.OnRemovingAsync(Workspace(), CancellationToken.None));
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8), "the start is cancelled, not waited out");
        IsRunning(pid).Should().BeFalse();
    }

    [SkippableFact]
    public async Task Removing_a_run_ends_its_server_process_even_when_the_cmd_wrapper_exited_first()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Only on Windows does the MCP SDK start a stdio server under a cmd.exe wrapper.");
        var workspace = Workspace() with { Root = Directory.CreateDirectory(Path.Combine(_root, "wrapped-ws")).FullName };
        // Outlives the end of its stdin, so only a kill ends it: the SDK's dispose closes stdin but kills nothing once its wrapper is gone.
        var registry = Registry(runScoped: new() { Args = [.. ServerArgs, "--shutdown-delay-ms", "60000"] });
        await registry.OnReadyAsync(workspace, CancellationToken.None);
        var pid = await PidAsync(registry);
        await KillWrapperOnlyAsync(pid); // the SDK waits for the wrapper alone, so it finds nothing left to stop

        await registry.OnRemovingAsync(workspace, CancellationToken.None);
        var running = IsRunning(pid);
        var workspaceFree = TryDelete(workspace.Root);

        using var _scope = new AssertionScope();
        running.Should().BeFalse("the removal ends the server process itself, not only the wrapper the SDK started it under");
        workspaceFree.Should().BeTrue("once the removal returns, no process has the run's workspace as its working directory");
    }

    [SkippableFact]
    public async Task Removing_a_run_while_its_server_is_starting_ends_the_server_process_even_when_the_cmd_wrapper_exited_first()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Only on Windows does the MCP SDK start a stdio server under a cmd.exe wrapper.");
        var workspace = Workspace() with { Root = Directory.CreateDirectory(Path.Combine(_root, "wrapped-ws")).FullName };
        var pidFile = Path.Combine(_root, "server.pid");
        var registry = Registry(runScoped: new() { Args = [.. ServerArgs, "--pid-file", pidFile, "--delay-ms", "60000"] });
        await registry.OnReadyAsync(workspace, CancellationToken.None);
        var pid = await PidFromFileAsync(pidFile); // silent for 60 s: its client is still connecting
        await KillWrapperOnlyAsync(pid);

        await registry.OnRemovingAsync(workspace, CancellationToken.None);
        var running = IsRunning(pid);
        var workspaceFree = TryDelete(workspace.Root);

        using var _scope = new AssertionScope();
        running.Should().BeFalse("the removal ends a server whose client never connected, not only the wrapper the SDK started it under");
        workspaceFree.Should().BeTrue("once the removal returns, no process has the run's workspace as its working directory");
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public async Task A_stop_waits_past_the_shutdown_timeout_for_its_killed_server_to_finish_exiting()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Only on Windows does the registry end the server's process tree itself.");
        var events = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(events));
        var definition = McpServerFixture.Definition("--host");
        definition.ShutdownTimeout = TimeSpan.FromSeconds(1);
        definition.RunScoped = new() { Args = [.. ServerArgs, "--shutdown-delay-ms", "60000"] }; // only a kill ends it
        var registry = new RunMcpServerRegistry(Servers(definition), () => new FakeProvider(null), loggerFactory, TimeProvider.System);
        _registries.Add(registry);

        // Each killed process's exit signal arrives 2.5 s after the wait for it begins: a teardown that lags past the
        // 1 s shutdown timeout, as one can under load.
        var signalDelay = TimeSpan.FromSeconds(2.5);
        var delayedSignals = 0;
        registry.WaitForProcessExit = async (process, wait) =>
        {
            if (wait < signalDelay)
            {
                await Task.Delay(wait);
                return false;
            }

            await Task.Delay(signalDelay);
            Interlocked.Increment(ref delayedSignals);
            return await ServerProcessTree.WaitForExitAsync(process, wait - signalDelay);
        };

        await registry.OnReadyAsync(Workspace(), CancellationToken.None);
        var pid = await PidAsync(registry);
        await KillWrapperOnlyAsync(pid); // the server outlives the SDK's dispose, so the registry's own kill ends it
        await registry.OnRemovingAsync(Workspace(), CancellationToken.None);

        using var _scope = new AssertionScope();
        delayedSignals.Should().BeGreaterThan(0, "the server's process was killed and its delayed exit was waited for");
        events.Count(ProcessTreeNotEndedEvent).Should().Be(0, "the wait for a killed process to finish exiting is not cut short by the shutdown timeout");
        IsRunning(pid).Should().BeFalse();
    }

    private static readonly EventId ProcessTreeNotEndedEvent = new(322);

    [Fact]
    public async Task Disposing_the_registry_while_a_server_is_starting_stops_it_promptly_and_leaves_no_process()
    {
        var pidFile = Path.Combine(_root, "server.pid");
        var registry = Registry(runScoped: new() { Args = [.. ServerArgs, "--pid-file", pidFile, "--delay-ms", "15000"] });
        await registry.OnReadyAsync(Workspace(), CancellationToken.None);
        var pid = await PidFromFileAsync(pidFile);

        var sw = Stopwatch.StartNew();
        await Task.Run(async () => await registry.DisposeAsync());
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8), "the start is cancelled, not waited out");
        IsRunning(pid).Should().BeFalse();
        var act = async () => await registry.GetReadyClientAsync("roslyn", RunId, CancellationToken.None);
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Disposing_the_registry_waits_for_a_removal_that_is_already_stopping_its_servers()
    {
        var registry = Registry(runScoped: new() { Args = ServerArgs });
        await registry.OnReadyAsync(Workspace(), CancellationToken.None);
        var pid = await PidAsync(registry);

        var removal = registry.OnRemovingAsync(Workspace(), CancellationToken.None).AsTask(); // takes the run's servers at once, then stops them
        await registry.DisposeAsync();
        IsRunning(pid).Should().BeFalse("dispose returns only once the removal's stop is done, so the host cannot exit before its child");
        await removal;
    }

    [Fact]
    public async Task Disposing_the_registry_stops_every_run_server()
    {
        var registry = Registry(runScoped: new() { Args = ServerArgs });
        var other = Workspace() with { RunId = Guid.NewGuid() };
        await registry.OnReadyAsync(Workspace(), CancellationToken.None);
        await registry.OnReadyAsync(other, CancellationToken.None);
        var pids = new[] { await PidAsync(registry), await PidAsync(registry, other.RunId) };

        await registry.DisposeAsync();
        pids.Where(IsRunning).Should().BeEmpty();
    }

    // ---------- helpers ----------

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var registry in _registries)
        {
            await registry.DisposeAsync();
        }

        Directory.Delete(_root, recursive: true);
    }

    private RunMcpServerRegistry Registry(RunScopedMcpDefinition runScoped) => Registry(runScoped, new FakeProvider(null));

    private RunMcpServerRegistry Registry(RunScopedMcpDefinition runScoped, IRunWorkspaceProvider? workspaces, IReadOnlyDictionary<string, string>? hostEnv = null)
    {
        // The host entry's own args start a working server, so a registry that ignores runScoped.Args fails an assertion, not the start.
        var definition = McpServerFixture.Definition("--host");
        definition.Env = hostEnv;
        definition.RunScoped = runScoped;
        var registry = new RunMcpServerRegistry(Servers(definition), () => workspaces, NullLoggerFactory.Instance, TimeProvider.System);
        _registries.Add(registry);
        return registry;
    }

    private static Dictionary<string, McpServerDefinition> Servers(McpServerDefinition definition) =>
        new(StringComparer.Ordinal) { ["roslyn"] = definition };

    private RunWorkspace Workspace(string? solution = null) => new(RunId, "repo", "https://example.invalid/repo.git", "main", "run/branch", _root, solution);

    private static FakeProvider ProviderThatFinds(RunWorkspace? workspace) => new(workspace);

    private async Task<RunMcpClientLease> LeaseAsync(RunMcpServerRegistry registry, Guid? runId = null)
    {
        var lease = await registry.GetReadyClientAsync("roslyn", runId ?? RunId, CancellationToken.None);
        lease.IsSuccess.Should().BeTrue(lease.IsFailure ? lease.Error.Message : "");
        return lease.Value;
    }

    private async Task<int> PidAsync(RunMcpServerRegistry registry, Guid? runId = null)
    {
        await using var lease = await LeaseAsync(registry, runId);
        return int.Parse(await CallAsync(lease.Client, "pid"), CultureInfo.InvariantCulture);
    }

    private static async Task<string> CallAsync(McpClient client, string tool, IReadOnlyDictionary<string, object?>? arguments = null)
    {
        var result = await client.CallToolAsync(tool, arguments);
        return result.Content.OfType<TextContentBlock>().Single().Text; // an error result's text fails the caller's value assertion
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no process has that id any more
        }
    }

    /// <summary>
    /// Kills the <c>cmd.exe</c> the MCP SDK started server <paramref name="pid"/> under, and only it: the server lives on,
    /// holding the session's pipes, so the SDK sees nothing wrong until it disposes the client.
    /// </summary>
    private static async Task KillWrapperOnlyAsync(int pid)
    {
        var query = new ProcessStartInfo("powershell", $"-NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').ParentProcessId\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        int wrapperPid;
        using (var powershell = Process.Start(query)!)
        {
            wrapperPid = int.Parse((await powershell.StandardOutput.ReadToEndAsync()).Trim(), CultureInfo.InvariantCulture);
            await powershell.WaitForExitAsync();
        }

        using var wrapper = Process.GetProcessById(wrapperPid);
        wrapper.ProcessName.Should().Be("cmd", "the SDK starts a stdio server as cmd.exe /c on Windows");
        wrapper.Kill(entireProcessTree: false);
        await wrapper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        IsRunning(pid).Should().BeTrue("only the wrapper was killed");
    }

    private static bool TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task<int> PidFromFileAsync(string pidFile)
    {
        await UntilAsync(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0, "the server to write its pid");
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return int.Parse(await File.ReadAllTextAsync(pidFile), CultureInfo.InvariantCulture);
            }
            catch (IOException) when (sw.Elapsed < TimeSpan.FromSeconds(20))
            {
                await Task.Delay(50); // the server may still hold the file open on Windows while it finishes writing
            }
        }
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20), $"waiting for {what}");
            await Task.Delay(50);
        }
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>Records the event id of every log entry, in order, so a test can tell what the registry did and when.</summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<int> _events = new();

        public int Count(EventId eventId) => _events.Count(id => id == eventId.Id);

        public ILogger CreateLogger(string categoryName) => new Recorder(_events);

        public void Dispose()
        {
        }

        private sealed class Recorder(ConcurrentQueue<int> events) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                events.Enqueue(eventId.Id);
        }
    }

    /// <summary>Counts every call to every provider method; <see cref="HoldFinds"/> parks <see cref="FindAsync"/> until <see cref="ReleaseFinds"/>.</summary>
    private sealed class FakeProvider(RunWorkspace? found) : IRunWorkspaceProvider
    {
        private int _calls;
        private TaskCompletionSource? _hold;

        public int Calls => Volatile.Read(ref _calls);

        public TaskCompletionSource FindStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void HoldFinds() => Volatile.Write(ref _hold, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public void ReleaseFinds() => Interlocked.Exchange(ref _hold, null)?.TrySetResult();

        public ValueTask<Result<RunWorkspace, AgentError>> CreateAsync(RunWorkspaceRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return new(Result<RunWorkspace, AgentError>.Failure(AgentError.Validation("the fake creates nothing")));
        }

        public async ValueTask<RunWorkspace?> FindAsync(Guid runId, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            FindStarted.TrySetResult();
            if (Volatile.Read(ref _hold) is { } hold)
            {
                await hold.Task.WaitAsync(ct);
            }

            return found is not null && found.RunId == runId ? found : null;
        }

        public ValueTask<IReadOnlyList<RunWorkspace>> ListAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return new(found is null ? [] : [found]);
        }

        public ValueTask<UnitResult<AgentError>> RemoveAsync(Guid runId, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return new(UnitResult<AgentError>.Success());
        }
    }
}
