using System.Diagnostics;
using AwesomeAssertions.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos.Mcp;
using Thalos.Workspaces;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;
using static Thalos.Tests.Mcp.TestCallers;

namespace Thalos.Tests.Mcp;

/// <summary>
/// <see cref="RunScopedMcpToolSource"/>'s routing, called directly inside a turn scope. The host server's arguments
/// carry <c>--host</c>; each run's server carries <c>--run --id {run id}</c>, so the <c>args</c> tool names the process
/// that answered.
/// </summary>
public sealed class RunScopedMcpToolSourceTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("thalos-run-routed-").FullName;
    private readonly List<IAsyncDisposable> _disposables = [];

    private Guid RunId { get; } = Guid.NewGuid();

    // ---------- the brief's three routing facts ----------

    [Fact]
    public async Task A_chat_caller_is_served_by_the_host_server()
    {
        var (tool, _) = await RoutedToolAsync("args");
        using var _turn = BeginTurn(new TestCaller("chat-user"));
        (await InvokeAsync(tool)).Should().Contain("--host");
    }

    [Fact]
    public async Task A_run_caller_is_served_by_its_run_server()
    {
        var (tool, _) = await RoutedToolAsync("args", ready: [RunId]);
        using var _turn = BeginTurn(RunCaller(RunId));
        using var _scope = new AssertionScope();
        var result = await InvokeAsync(tool);
        result.Should().Contain("--run");
        result.Should().Contain($"--id {RunId:D}");
        result.Should().NotContain("--host");
    }

    [Fact]
    public async Task A_run_caller_with_no_ready_run_server_is_refused_and_the_host_is_never_called()
    {
        var (tool, _) = await RoutedToolAsync("args");
        using var _turn = BeginTurn(RunCaller(RunId));
        var result = await InvokeAsync(tool);
        using var _scope = new AssertionScope();
        result.Should().StartWith("error: run tool server 'roslyn' is not available for this run: ");
        result.Should().NotContain("--host");
    }

    // ---------- who counts as a run caller ----------

    [Fact]
    public async Task A_call_outside_any_turn_is_served_by_the_host_server_as_before()
    {
        var (tool, _) = await RoutedToolAsync("args", ready: [RunId]);
        (await InvokeAsync(tool)).Should().Contain("--host");
    }

    [Fact]
    public async Task A_caller_whose_run_claim_is_not_a_valid_id_is_refused_not_served_by_the_host()
    {
        var (tool, _) = await RoutedToolAsync("args", ready: [RunId]);
        using var _turn = BeginTurn(new TestCaller("forged", new Dictionary<string, string>(StringComparer.Ordinal) { [RunWorkspaceClaims.RunId] = "not-a-guid" }));
        var result = await InvokeAsync(tool);
        using var _scope = new AssertionScope();
        result.Should().StartWith("error: run tool server 'roslyn' is not available for this run: the caller's run claim is not a valid run id");
        result.Should().NotContain("--host");
    }

    [Fact]
    public async Task Arguments_naming_another_run_do_not_change_which_server_answers()
    {
        var other = Guid.NewGuid();
        var (tool, _) = await RoutedToolAsync("args", ready: [RunId, other]);
        using var _turn = BeginTurn(RunCaller(RunId));
        var crafted = new AIFunctionArguments(StringComparer.Ordinal)
        {
            [RunWorkspaceClaims.RunId] = other.ToString("D"),
            ["runId"] = other.ToString("D"),
            ["caller"] = "host",
        };

        var result = await InvokeAsync(tool, crafted);

        using var _scope = new AssertionScope();
        result.Should().Contain($"--id {RunId:D}", "only the turn's caller chooses the server");
        result.Should().NotContain(other.ToString("D"));
        result.Should().NotContain("--host");
    }

    // ---------- a run's server that is missing, slow or failed ----------

    [Fact]
    public async Task A_server_still_starting_past_the_ready_wait_is_refused_within_that_wait()
    {
        var runScoped = RunScoped("--delay-ms", "20000");
        runScoped.ReadyWaitTimeout = TimeSpan.FromSeconds(1);
        var (tool, registry) = await RoutedToolAsync("args", runScoped);
        await registry.OnReadyAsync(Workspace(RunId), CancellationToken.None); // starts, and stays silent for 20 s

        using var _turn = BeginTurn(RunCaller(RunId));
        var sw = Stopwatch.StartNew();
        var result = await InvokeAsync(tool);

        using var _scope = new AssertionScope();
        result.Should().Be($"error: run tool server 'roslyn' is not available for this run: its server was not ready within {TimeSpan.FromSeconds(1)}.");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the wait is bounded by readyWaitTimeout, not by the server's start");
    }

    [Fact]
    public async Task A_run_server_whose_restart_failed_refuses_every_call_and_never_falls_back_to_the_host()
    {
        var marker = Path.Combine(_root, "failed-once");
        var runScoped = RunScoped("--fail-first-start", marker);
        runScoped.Reload = "restart";
        var (tool, registry) = await RoutedToolAsync("args", runScoped);
        await registry.OnReadyAsync(Workspace(RunId), CancellationToken.None); // the first start fails and writes the marker
        var waited = await registry.WaitAllReadyAsync(RunId, TimeSpan.FromSeconds(30), CancellationToken.None); // reports the failed first start
        if (waited.IsFailure)
        {
            waited = await registry.WaitAllReadyAsync(RunId, TimeSpan.FromSeconds(30), CancellationToken.None); // starts it again
        }

        waited.IsSuccess.Should().BeTrue("the second start finds the marker");

        File.Delete(marker); // the restart's process now fails
        registry.OnFilesChanged(RunId, ["a.cs"]);
        using var _turn = BeginTurn(RunCaller(RunId));
        var first = await InvokeAsync(tool);
        var second = await InvokeAsync(tool);

        using var _scope = new AssertionScope();
        first.Should().StartWith("error: run tool server 'roslyn' is not available for this run: ");
        first.Should().Contain("failed to start");
        first.Should().NotContain("--host");
        second.Should().StartWith("error: run tool server 'roslyn' is not available for this run: ");
        second.Should().Contain("failed to start");
        second.Should().NotContain("--host");
    }

    [Fact]
    public async Task A_call_past_the_call_timeout_is_cancelled_and_its_lease_is_released()
    {
        var runScoped = RunScoped();
        runScoped.Reload = "tool:reload_count";
        runScoped.CallTimeout = TimeSpan.FromSeconds(1);
        runScoped.ReadyWaitTimeout = TimeSpan.FromSeconds(5);
        var (slow, registry) = await RoutedToolAsync("slow", runScoped, ready: [RunId]);
        var reloadCount = await ToolAsync(slow, "reload_count");

        using var _turn = BeginTurn(RunCaller(RunId));
        var sw = Stopwatch.StartNew();
        var timedOut = await InvokeAsync(slow, Args("ms", 20000));
        var elapsed = sw.Elapsed;

        using var _scope = new AssertionScope();
        timedOut.Should().Be($"error: run tool server 'roslyn' did not answer 'slow' within {TimeSpan.FromSeconds(1)} for this run; the call was cancelled.");
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the call is cancelled at the timeout, not reported after it finished");

        registry.OnFilesChanged(RunId, ["a.cs"]); // the reload waits for every lease: a leaked one would hold it past the ready wait
        (await InvokeAsync(reloadCount)).Should().Be("2", "the reload ran, so the timed-out call's lease was released");
    }

    [Fact]
    public async Task A_call_cancelled_by_its_caller_throws_and_releases_its_lease()
    {
        var runScoped = RunScoped();
        runScoped.Reload = "tool:reload_count";
        runScoped.ReadyWaitTimeout = TimeSpan.FromSeconds(5);
        var (slow, registry) = await RoutedToolAsync("slow", runScoped, ready: [RunId]);
        var reloadCount = await ToolAsync(slow, "reload_count");

        using var _turn = BeginTurn(RunCaller(RunId));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var call = async () => await slow.InvokeAsync(Args("ms", 20000), cts.Token);
        await call.Should().ThrowAsync<OperationCanceledException>("the caller's own cancellation is not turned into a result");

        registry.OnFilesChanged(RunId, ["a.cs"]);
        (await InvokeAsync(reloadCount)).Should().Be("2", "the reload ran, so the cancelled call's lease was released");
    }

    [Fact]
    public async Task A_call_in_flight_when_its_run_is_removed_gets_an_error_result_not_a_cancellation()
    {
        var log = Path.Combine(_root, "calls.log");
        var (slow, registry) = await RoutedToolAsync("slow", RunScoped("--call-log", log), ready: [RunId]);

        var inFlight = Task.Run(async () =>
        {
            using var _turn = BeginTurn(RunCaller(RunId));
            try
            {
                return await InvokeAsync(slow, Args("ms", 20000));
            }
            catch (OperationCanceledException ex)
            {
                return $"threw {ex.GetType().Name}";
            }
        });
        await UntilAsync(() => File.Exists(log) && File.ReadAllText(log).Contains("slow", StringComparison.Ordinal), "the slow call to reach the run's server");
        await registry.OnRemovingAsync(Workspace(RunId), CancellationToken.None);

        (await inFlight.WaitAsync(TimeSpan.FromSeconds(15))).Should().Be("error: run tool server 'roslyn' stopped during 'slow' for this run; the call did not complete.");
    }

    // ---------- concurrency ----------

    [Fact]
    public async Task Parallel_calls_from_two_runs_and_a_chat_caller_are_each_served_by_their_own_server()
    {
        var other = Guid.NewGuid();
        var (tool, _) = await RoutedToolAsync("args", ready: [RunId, other]);
        var callers = new (ISecurityContext Caller, string Expected, string[] Forbidden)[]
        {
            (RunCaller(RunId), $"--id {RunId:D}", ["--host", other.ToString("D")]),
            (RunCaller(other), $"--id {other:D}", ["--host", RunId.ToString("D")]),
            (new TestCaller("chat-user"), "--host", ["--run"]),
        };

        var calls = Enumerable.Range(0, 8).SelectMany(_ => callers).Select(c => Task.Run(async () =>
        {
            using var _turn = BeginTurn(c.Caller);
            return (c.Expected, c.Forbidden, Result: await InvokeAsync(tool));
        })).ToArray();

        var results = await Task.WhenAll(calls);
        using var _scope = new AssertionScope();
        foreach (var (expected, forbidden, result) in results)
        {
            result.Should().Contain(expected);
            foreach (var marker in forbidden)
            {
                result.Should().NotContain(marker);
            }
        }
    }

    [Fact]
    public async Task Calls_racing_a_run_removal_are_served_by_the_run_or_refused_never_by_the_host()
    {
        var (tool, registry) = await RoutedToolAsync("args", ready: [RunId]);
        using var start = new ManualResetEventSlim();
        var calls = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            using var _turn = BeginTurn(RunCaller(RunId));
            start.Wait(TimeSpan.FromSeconds(10));
            try
            {
                return await InvokeAsync(tool);
            }
            catch (Exception ex)
            {
                return $"threw {ex.GetType().Name}"; // collected, so every outcome is asserted below
            }
        })).ToArray();
        var removal = Task.Run(async () =>
        {
            start.Wait(TimeSpan.FromSeconds(10));
            await registry.OnRemovingAsync(Workspace(RunId), CancellationToken.None);
        });

        start.Set();
        await removal;
        var results = await Task.WhenAll(calls);

        using var _after = BeginTurn(RunCaller(RunId));
        var afterRemoval = await InvokeAsync(tool);

        using var _scope = new AssertionScope();
        results.Should().OnlyContain(r => r.Contains($"--id {RunId:D}", StringComparison.Ordinal) || r.StartsWith("error: run tool server", StringComparison.Ordinal) || r.StartsWith("threw", StringComparison.Ordinal));
        results.Should().NotContain(r => r.Contains("--host", StringComparison.Ordinal));
        results.Should().NotContain(r => r.Contains("Canceled", StringComparison.Ordinal), "a call cut off by the removal is an error result, never a cancellation the caller did not ask for");
        afterRemoval.Should().StartWith("error: run tool server 'roslyn' is not available for this run: ", "a removed run's calls are refused");
    }

    [Fact]
    public async Task Calls_racing_reloads_all_reach_the_run_server_and_never_overlap_a_reload()
    {
        var log = Path.Combine(_root, "calls.log");
        var runScoped = RunScoped("--call-log", log, "--reload-delay-ms", "200");
        runScoped.Reload = "tool:reload_count";
        var (slow, registry) = await RoutedToolAsync("slow", runScoped, ready: [RunId]);
        var overlaps = await ToolAsync(slow, "overlaps");

        using var start = new ManualResetEventSlim();
        var calls = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            using var _turn = BeginTurn(RunCaller(RunId));
            start.Wait(TimeSpan.FromSeconds(10));
            var results = new List<string>();
            var arguments = Args("ms", 100);
            for (var i = 0; i < 4; i++)
            {
                results.Add(await InvokeAsync(slow, arguments));
            }

            return results;
        })).ToArray();
        var changes = Task.Run(async () =>
        {
            start.Wait(TimeSpan.FromSeconds(10));
            for (var i = 0; i < 5; i++)
            {
                registry.OnFilesChanged(RunId, [$"f{i}.cs"]);
                await Task.Delay(100);
            }
        });

        start.Set();
        await changes;
        var results = (await Task.WhenAll(calls)).SelectMany(r => r).ToList();

        var logged = File.Exists(log) ? await File.ReadAllLinesAsync(log) : [];
        using var _turn = BeginTurn(RunCaller(RunId));
        var overlapCount = await InvokeAsync(overlaps);

        using var _scope = new AssertionScope();
        results.Should().OnlyContain(r => r.Contains("slow done", StringComparison.Ordinal));
        logged.Count(l => string.Equals(l, "slow", StringComparison.Ordinal)).Should().Be(24, "every call reached the run's server, which alone writes the log");
        logged.Should().Contain("reload_count", "the changes were reloaded while calls were being made");
        overlapCount.Should().Be("0", "a reload waits for every routed call's lease");
    }

    // ---------- construction ----------

    [Theory]
    [InlineData(0, 60)]
    [InlineData(60, -1)]
    public void A_non_positive_timeout_is_rejected_at_construction(int readySeconds, int callSeconds)
    {
        var runScoped = RunScoped();
        runScoped.ReadyWaitTimeout = TimeSpan.FromSeconds(readySeconds);
        runScoped.CallTimeout = TimeSpan.FromSeconds(callSeconds);
        var act = () => new RunScopedMcpToolSource(
            new McpToolSource("roslyn", McpServerFixture.Definition(), NullLoggerFactory.Instance), runScoped,
            Registry(RunScoped()), TimeProvider.System, NullLogger<RunScopedMcpToolSource>.Instance);
        act.Should().Throw<ArgumentException>().WithParameterName("runScoped").WithMessage(readySeconds <= 0 ? "*readyWaitTimeout*" : "*callTimeout*");
    }

    [Fact]
    public async Task The_routed_tools_keep_the_host_tools_names_and_schemas()
    {
        var host = new McpToolSource("roslyn", McpServerFixture.Definition("--host"), NullLoggerFactory.Instance);
        _disposables.Add(host);
        var hostTools = (await host.GetToolsAsync(CancellationToken.None)).Value.Cast<AIFunction>().ToList();
        var runScoped = RunScoped();
        var source = Source(runScoped, Registry(runScoped));
        var routed = (await source.GetToolsAsync(CancellationToken.None)).Value.Cast<AIFunction>().ToList();

        using var _scope = new AssertionScope();
        routed.Select(t => t.Name).Should().Equal(hostTools.Select(t => t.Name));
        routed.Select(t => t.Description).Should().Equal(hostTools.Select(t => t.Description));
        routed.Select(t => t.JsonSchema.GetRawText()).Should().Equal(hostTools.Select(t => t.JsonSchema.GetRawText()));
    }

    // ---------- helpers ----------

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        for (var i = _disposables.Count - 1; i >= 0; i--)
        {
            await _disposables[i].DisposeAsync();
        }

        Directory.Delete(_root, recursive: true);
    }

    private static RunScopedMcpDefinition RunScoped(params string[] extra) =>
        new() { Args = [McpServerFixture.ServerDll, "--run", "--id", "${run.id}", .. extra] };

    private RunMcpServerRegistry Registry(RunScopedMcpDefinition runScoped)
    {
        var definition = McpServerFixture.Definition("--host");
        definition.RunScoped = runScoped;
        var registry = new RunMcpServerRegistry(
            new Dictionary<string, McpServerDefinition>(StringComparer.Ordinal) { ["roslyn"] = definition },
            () => new NoRecordsProvider(), NullLoggerFactory.Instance, TimeProvider.System);
        _disposables.Add(registry);
        return registry;
    }

    /// <summary>The routed tool <paramref name="toolName"/>, with a server started and ready for each run in <paramref name="ready"/>.</summary>
    private async Task<(AIFunction Tool, RunMcpServerRegistry Registry)> RoutedToolAsync(string toolName, RunScopedMcpDefinition? runScoped = null, Guid[]? ready = null)
    {
        runScoped ??= RunScoped();
        var registry = Registry(runScoped);
        var source = Source(runScoped, registry);

        foreach (var runId in ready ?? [])
        {
            await registry.OnReadyAsync(Workspace(runId), CancellationToken.None);
            var waited = await registry.WaitAllReadyAsync(runId, TimeSpan.FromSeconds(30), CancellationToken.None);
            waited.IsSuccess.Should().BeTrue(waited.IsFailure ? waited.Error.Message : "");
        }

        var tools = await source.GetToolsAsync(CancellationToken.None);
        tools.IsSuccess.Should().BeTrue(tools.IsFailure ? tools.Error.Message : "");
        return (tools.Value.OfType<AIFunction>().Where(t => string.Equals(t.Name, toolName, StringComparison.Ordinal)).Should().ContainSingle().Subject, registry);
    }

    private RunScopedMcpToolSource Source(RunScopedMcpDefinition runScoped, RunMcpServerRegistry registry)
    {
        var definition = McpServerFixture.Definition("--host");
        definition.RunScoped = runScoped;
        var source = new RunScopedMcpToolSource(
            new McpToolSource("roslyn", definition, NullLoggerFactory.Instance), runScoped, registry, TimeProvider.System, NullLogger<RunScopedMcpToolSource>.Instance);
        _disposables.Add(source);
        return source;
    }

    /// <summary>Another tool of the same source as <paramref name="sibling"/>'s; the source caches its wrapped list, so it is found by name.</summary>
    private async Task<AIFunction> ToolAsync(AIFunction sibling, string toolName)
    {
        var source = _disposables.OfType<RunScopedMcpToolSource>().Last();
        var tools = (await source.GetToolsAsync(CancellationToken.None)).Value;
        tools.Should().Contain(sibling);
        return (AIFunction)tools.Single(t => string.Equals(t.Name, toolName, StringComparison.Ordinal));
    }

    private static AIFunctionArguments Args(string name, object value) => new(StringComparer.Ordinal) { [name] = value };

    private static async Task<string> InvokeAsync(AIFunction tool, AIFunctionArguments? arguments = null) =>
        (await tool.InvokeAsync(arguments ?? new AIFunctionArguments(StringComparer.Ordinal), CancellationToken.None))!.ToString()!;

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20), $"waiting for {what}");
            await Task.Delay(50);
        }
    }

    private RunWorkspace Workspace(Guid runId) => new(runId, "repo", "https://example.invalid/repo.git", "main", "run/branch", _root, null);

    /// <summary>A provider with no records: the tests start run servers through <see cref="RunMcpServerRegistry.OnReadyAsync"/>.</summary>
    private sealed class NoRecordsProvider : IRunWorkspaceProvider
    {
        public ValueTask<Result<RunWorkspace, AgentError>> CreateAsync(RunWorkspaceRequest request, CancellationToken ct) =>
            new(Result<RunWorkspace, AgentError>.Failure(AgentError.Validation("the fake creates nothing")));

        public ValueTask<RunWorkspace?> FindAsync(Guid runId, CancellationToken ct) => new((RunWorkspace?)null);

        public ValueTask<IReadOnlyList<RunWorkspace>> ListAsync(CancellationToken ct) => new([]);

        public ValueTask<UnitResult<AgentError>> RemoveAsync(Guid runId, CancellationToken ct) => new(UnitResult<AgentError>.Success());
    }
}
