using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Protocol;
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

    /// <summary>
    /// A call to a run whose server is still starting is refused when its ready wait is over, not when the server answers.
    /// The ready wait runs on a fake clock that this test moves on, and the server stays silent for ten minutes, so the
    /// only thing that can end the call with this refusal is the ready wait; a call that waited for the start instead
    /// would end only when the SDK's 60 s initialization timeout fails the start, with a different refusal. Decided by the
    /// clock and the refusal, not by wall time: on a loaded Windows runner other tests' stdio sessions pin thread-pool
    /// threads in blocking pipe reads, which has delayed a correct refusal past a 10 s bound.
    /// Red: in RunScopedMcpToolSource.InvokeForRunAsync, pass <c>ct</c> to GetReadyClientAsync instead of the token linked
    /// to the ready timeout.
    /// </summary>
    [Fact]
    public async Task A_server_still_starting_past_the_ready_wait_is_refused_within_that_wait()
    {
        var runScoped = RunScoped("--delay-ms", "600000");
        runScoped.ReadyWaitTimeout = TimeSpan.FromSeconds(1);
        var clock = new FakeTimeProvider();
        var (tool, registry) = await RoutedToolAsync("args", runScoped, clock: clock);
        await registry.OnReadyAsync(Workspace(RunId), CancellationToken.None); // starts, and stays silent for ten minutes

        using var _turn = BeginTurn(RunCaller(RunId));
        var result = await PastTheReadyWaitAsync(InvokeAsync(tool), clock);

        result.Should().Be($"error: run tool server 'roslyn' is not available for this run: its server was not ready within {TimeSpan.FromSeconds(1)}.");
    }

    [Fact]
    public async Task A_run_server_whose_restart_failed_refuses_every_call_and_never_falls_back_to_the_host()
    {
        var marker = Path.Combine(_root, "failed-once");
        var runScoped = RunScoped("--fail-first-start", marker);
        runScoped.Reload = "restart";
        var (tool, registry) = await RoutedToolAsync("args", runScoped);
        await registry.OnReadyAsync(Workspace(RunId), CancellationToken.None); // the first start fails and writes the marker
        var waited = await registry.WaitAllReadyAsync(RunId, McpServerFixture.StartupBudget, CancellationToken.None); // reports the failed first start
        if (waited.IsFailure)
        {
            waited = await registry.WaitAllReadyAsync(RunId, McpServerFixture.StartupBudget, CancellationToken.None); // starts it again
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

    /// <summary>
    /// A call past its call timeout is cancelled and reported, and its lease released. Both timeouts run on a fake clock:
    /// the call timeout fires when this test moves the clock, and the ready wait of the call after the reload never does,
    /// so that call waits for the reload however slow the machine is. The slow call takes ten minutes, so only the timeout
    /// can end it within the hang guard; that the timeout ended it is shown by the report, not by wall time.
    /// Red, the report: in RunScopedMcpToolSource.InvokeForRunAsync, link only <c>ct</c> into the call's token, not the
    /// call timeout; the call then outlives the hang guard.
    /// Red, the lease released: in InvokeForRunAsync, hold the lease without <c>await using</c>; the reload then waits
    /// for it forever.
    /// </summary>
    [Fact]
    public async Task A_call_past_the_call_timeout_is_cancelled_and_its_lease_is_released()
    {
        var log = Path.Combine(_root, "calls.log");
        var runScoped = RunScoped("--call-log", log);
        runScoped.Reload = "tool:reload_count";
        runScoped.CallTimeout = TimeSpan.FromSeconds(1);
        runScoped.ReadyWaitTimeout = TimeSpan.FromSeconds(5);
        var clock = new FakeTimeProvider();
        var (slow, registry) = await RoutedToolAsync("slow", runScoped, ready: [RunId], clock: clock);
        var reloadCount = await ToolAsync(slow, "reload_count");

        using var _turn = BeginTurn(RunCaller(RunId));
        var call = InvokeAsync(slow, Args("ms", 600000));
        await UntilAsync(() => CallLog.Read(log).Contains("slow", StringComparison.Ordinal), "the slow call to reach the run's server"); // its call timeout has started
        clock.Advance(runScoped.CallTimeout);
        var timedOut = await call.WaitAsync(HangGuard);

        timedOut.Should().Be($"error: run tool server 'roslyn' did not answer 'slow' within {TimeSpan.FromSeconds(1)} for this run; the call was cancelled.");

        registry.OnFilesChanged(RunId, ["a.cs"]); // the reload waits for every lease: a leaked one would hold it forever
        (await InvokeAsync(reloadCount).WaitAsync(TimeSpan.FromSeconds(30))).Should().Be("2", "the reload ran, so the timed-out call's lease was released");
    }

    [Fact]
    public async Task A_call_cancelled_by_its_caller_throws_and_releases_its_lease()
    {
        // The routed calls' timeouts run on a fake clock that never moves: the call after the reload waits for it however
        // slow the machine is, and only the caller's own cancellation ends the slow call.
        var runScoped = RunScoped();
        runScoped.Reload = "tool:reload_count";
        var (slow, registry) = await RoutedToolAsync("slow", runScoped, ready: [RunId], clock: new FakeTimeProvider());
        var reloadCount = await ToolAsync(slow, "reload_count");

        using var _turn = BeginTurn(RunCaller(RunId));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var call = async () => await slow.InvokeAsync(Args("ms", 20000), cts.Token);
        await call.Should().ThrowAsync<OperationCanceledException>("the caller's own cancellation is not turned into a result");

        registry.OnFilesChanged(RunId, ["a.cs"]);
        (await InvokeAsync(reloadCount).WaitAsync(TimeSpan.FromSeconds(30))).Should().Be("2", "the reload ran, so the cancelled call's lease was released");
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
        await UntilAsync(() => CallLog.Read(log).Contains("slow", StringComparison.Ordinal), "the slow call to reach the run's server");
        await registry.OnRemovingAsync(Workspace(RunId), CancellationToken.None);

        (await inFlight.WaitAsync(HangGuard)).Should().Be("error: run tool server 'roslyn' stopped during 'slow' for this run; the call did not complete.");
    }

    /// <summary>
    /// A call written to a server that is being stopped, and so never answered, ends with the server's session, not at
    /// its call timeout. The call timeout runs on a fake clock that never moves, so a call that waited for it would never
    /// end, and the hang guard, not a wall-clock bound, catches that; how long the session took to close is not asserted.
    /// The window in which the call is written but never answered is held open by construction, not by timing: the
    /// client's dispose waits up to <see cref="HangGuard"/> for the server to exit before it kills it, and the server
    /// exits only once this test creates its <c>--exit-when</c> file, after the server has logged the routed call.
    /// Red, the outcome: in RunScopedMcpToolSource.CallUntilSessionEndsAsync, await the invocation alone, without the
    /// race against the client's completion; the call then outlives the hang guard.
    /// Red, the window open: give the server a 1 ms shutdown timeout instead of <see cref="HangGuard"/>; the dispose
    /// then kills it at once, and the session has closed by the time the server's log shows the routed call.
    /// </summary>
    [Fact]
    public async Task A_call_sent_while_its_run_server_is_being_stopped_gets_an_error_result_instead_of_waiting_out_the_call_timeout()
    {
        var log = Path.Combine(_root, "calls.log");
        var exit = Path.Combine(_root, "server-may-exit");
        var runScoped = RunScoped("--call-log", log, "--exit-when", exit);
        runScoped.CallTimeout = TimeSpan.FromSeconds(30);
        // Disposing the client keeps the server's stdin open until the server exits or this long has passed: the window
        // in which a request is written but never answered, which ends only when this test creates the exit file.
        var (slow, registry) = await RoutedToolAsync("slow", runScoped, ready: [RunId], shutdownTimeout: HangGuard, clock: new FakeTimeProvider());
        var client = await ClientAsync(registry); // the client the routed calls below are handed

        // A probe call in flight on the same client: disposing the client cancels its pending requests, which ends the probe.
        var probe = Task.Run(async () =>
        {
            using var _turn = BeginTurn(RunCaller(RunId));
            return await InvokeAsync(slow, Args("ms", 60000));
        });
        await UntilAsync(() => Array.FindAll(CallLog.Lines(log), l => string.Equals(l, "slow", StringComparison.Ordinal)).Length == 1, "the probe call to reach the run's server");

        // Runs inside the routed call, after its lease is taken and before its request is sent: the removal has begun,
        // and the client has cancelled its pending requests, which ends the probe. The server's stdin stays open until
        // it exits, which it does only once this test creates the exit file.
        Task? removal = null;
        var hook = new RunWhenSerialized(() =>
        {
            removal = registry.OnRemovingAsync(Workspace(RunId), CancellationToken.None).AsTask();
            probe.Wait(HangGuard); // a hang guard: the hook runs synchronously inside the routed call
        });

        Task<string> routed;
        using (BeginTurn(RunCaller(RunId)))
        {
            routed = InvokeAsync(slow, new AIFunctionArguments(StringComparer.Ordinal) { ["ms"] = 600000, ["hook"] = hook });
        }

        await UntilAsync(() => Array.FindAll(CallLog.Lines(log), l => string.Equals(l, "slow", StringComparison.Ordinal)).Length == 2, "the routed call to reach the run's server, which never answers it");
        var windowOpen = !client.Completion.IsCompleted; // the server has the routed call, and the session is still open
        await File.WriteAllTextAsync(exit, ""); // the window closes only now: the server exits and its session ends
        var outcome = await routed.WaitAsync(HangGuard);
        await removal!;

        using var _scope = new AssertionScope();
        hook.Ran.Should().BeTrue("the hook began the removal inside the routed call");
        probe.IsCompleted.Should().BeTrue("the removal had cancelled the client's pending requests before the call was sent");
        windowOpen.Should().BeTrue("the server received the routed call while its session was still open, so the call was written but never answered");
        outcome.Should().Be("error: run tool server 'roslyn' stopped during 'slow' for this run; the call did not complete.", "a server the removal stopped is reported as stopped, not as exited");
    }

    /// <summary>
    /// The bound on a wait whose other outcome is never finishing, or finishing only after ten minutes: a hang guard,
    /// never the claim, which each test shows by how the call was reported. Two minutes, because on a loaded Windows
    /// runner every live stdio session in the test process pins thread-pool threads in blocking pipe reads, the pool
    /// injects no threads while the CPU is saturated, and a call cut off by a stop or a kill has taken over 15 s to be
    /// reported with nothing wrong.
    /// </summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task A_reload_slower_than_the_ready_wait_still_completes_and_a_later_call_is_served()
    {
        // The ready wait runs on a fake clock and the reload ends only when the gate file exists, so which calls overlap
        // the reload is decided by this test, not by how fast a loaded machine runs it.
        var log = Path.Combine(_root, "calls.log");
        var gate = Path.Combine(_root, "reload-gate");
        var runScoped = RunScoped("--reload-when", gate, "--call-log", log);
        runScoped.Reload = "tool:reload_count";
        runScoped.ReadyWaitTimeout = TimeSpan.FromSeconds(1);
        var clock = new FakeTimeProvider();
        var (args, registry) = await RoutedToolAsync("args", runScoped, ready: [RunId], clock: clock);
        var reloadCount = await ToolAsync(args, "reload_count");

        registry.OnFilesChanged(RunId, ["a.cs"]);
        using var _turn = BeginTurn(RunCaller(RunId));
        var first = InvokeAsync(args);
        await UntilAsync(() => CallLog.Read(log).Contains("reload_count", StringComparison.Ordinal), "the first call's reload to reach the run's server");
        var during = await PastTheReadyWaitAsync(first, clock); // began the reload, then gives up
        var stillDuring = await PastTheReadyWaitAsync(InvokeAsync(args), clock); // the reload is still held at the gate
        await File.WriteAllTextAsync(gate, "");
        var after = await InvokeAsync(args).WaitAsync(TimeSpan.FromSeconds(30)); // waits for the reload, on a clock that no longer moves
        var count = await InvokeAsync(reloadCount);
        var logged = CallLog.Lines(log);

        using var _scope = new AssertionScope();
        during.Should().Be($"error: run tool server 'roslyn' is not available for this run: its server was not ready within {TimeSpan.FromSeconds(1)}.");
        stillDuring.Should().Be($"error: run tool server 'roslyn' is not available for this run: its server was not ready within {TimeSpan.FromSeconds(1)}.", "the reload the first waiter began is still running, and a later caller waits for it instead of being served");
        after.Should().Contain($"--id {RunId:D}", "the reload completed, so the next call is served");
        count.Should().Be("2", "one reload, not restarted by each waiter, plus this call");
        logged.Should().Equal(["reload_count", "reload_count"], "the server ran the reload once, then this test's own call");
    }

    [Fact]
    public async Task A_caller_that_cancels_while_its_run_server_is_dead_gets_the_cancellation_not_an_error_result()
    {
        var (echo, registry) = await RoutedToolAsync("echo", ready: [RunId]);
        var probe = await registry.GetReadyClientAsync("roslyn", RunId, CancellationToken.None);
        probe.IsSuccess.Should().BeTrue(probe.IsFailure ? probe.Error.Message : "");
        var client = probe.Value.Client; // the client the routed call below is handed; watched to see its session end
        var pid = int.Parse(((TextContentBlock)(await client.CallToolAsync("pid")).Content.Single()).Text, CultureInfo.InvariantCulture);
        await probe.Value.DisposeAsync();

        // Runs inside the routed call, after its lease is taken and before the request is sent: the server dies, its
        // session ends, and then the caller cancels, as a run cancelled and torn down at once would.
        using var cts = new CancellationTokenSource();
        var hook = new RunWhenSerialized(() =>
        {
            using (var process = Process.GetProcessById(pid))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(15_000);
            }

            SpinWait.SpinUntil(() => client.Completion.IsCompleted, TimeSpan.FromSeconds(15));
            cts.Cancel();
        });

        string outcome;
        using (BeginTurn(RunCaller(RunId)))
        {
            try
            {
                outcome = (await echo.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { ["text"] = hook }, cts.Token))!.ToString()!;
            }
            catch (OperationCanceledException ex)
            {
                outcome = $"cancelled: {ex.GetType().Name}";
            }
            catch (Exception ex)
            {
                outcome = $"threw {ex.GetType().Name}: {ex.Message}";
            }
        }

        using var _scope = new AssertionScope();
        hook.Ran.Should().BeTrue("the hook killed the server and cancelled the caller inside the routed call");
        client.Completion.IsCompleted.Should().BeTrue("the server's session had ended when the caller cancelled");
        outcome.Should().StartWith("cancelled", "a caller's own cancellation propagates, even when its server is dead by then");
    }

    [Fact]
    public async Task A_run_server_killed_under_a_call_refuses_that_call_instead_of_throwing()
    {
        var log = Path.Combine(_root, "calls.log");
        var (slow, _) = await RoutedToolAsync("slow", RunScoped("--call-log", log), ready: [RunId]);
        var pid = int.Parse(await RoutedAsync(await ToolAsync(slow, "pid")), CultureInfo.InvariantCulture);

        var inFlight = Task.Run(async () =>
        {
            using var _turn = BeginTurn(RunCaller(RunId));
            try
            {
                return await InvokeAsync(slow, Args("ms", 20000));
            }
            catch (Exception ex)
            {
                return $"threw {ex.GetType().Name}: {ex.Message}";
            }
        });
        await UntilAsync(() => CallLog.Read(log).Contains("slow", StringComparison.Ordinal), "the slow call to reach the run's server");
        await KillAsync(pid);

        var result = await inFlight.WaitAsync(HangGuard);

        using var _scope = new AssertionScope();
        result.Should().Be("error: run tool server 'roslyn' is not available for this run: its server exited during 'slow'.", "a server that died is refused, not reported as stopped");
    }

    [Fact]
    public async Task A_killed_run_server_refuses_calls_with_the_error_text_until_readiness_restarts_it()
    {
        var (args, registry) = await RoutedToolAsync("args", ready: [RunId]);
        var pidTool = await ToolAsync(args, "pid");
        var pid = await KillAndWaitForSessionEndAsync(registry);

        var refused = await RoutedOrThrownAsync(args);
        var waited = await registry.WaitAllReadyAsync(RunId, McpServerFixture.StartupBudget, CancellationToken.None);
        var restartedPid = await RoutedOrThrownAsync(pidTool);
        var served = await RoutedOrThrownAsync(args);

        using var _scope = new AssertionScope();
        refused.Should().Be($"error: run tool server 'roslyn' is not available for this run: Run-scoped MCP server 'roslyn' for run {RunId} exited unexpectedly.");
        waited.IsSuccess.Should().BeTrue(waited.IsFailure ? waited.Error.Message : "readiness starts a dead server again");
        restartedPid.Should().MatchRegex("^[0-9]+$", "the pid tool is served again");
        restartedPid.Should().NotBe(pid.ToString(CultureInfo.InvariantCulture), "a new process serves the run");
        served.Should().Contain($"--id {RunId:D}");
        IsRunning(pid).Should().BeFalse("the dead process is not left behind");
    }

    [Fact]
    public async Task A_killed_run_server_is_not_reported_ready_before_it_is_started_again()
    {
        var (_, registry) = await RoutedToolAsync("args", ready: [RunId]);
        await KillAndWaitForSessionEndAsync(registry);

        var waited = await registry.WaitAllReadyAsync(RunId, TimeSpan.Zero, CancellationToken.None);

        using var _scope = new AssertionScope();
        waited.IsFailure.Should().BeTrue("the only server there was is dead, and its replacement has not started yet");
        if (waited.IsFailure)
        {
            waited.Error.Message.Should().Contain("was not ready within", "readiness began a new start instead of reporting the dead one");
        }
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
        var servedMarker = $"--id {RunId:D}";
        var served = 0;
        using var start = new ManualResetEventSlim();
        var calls = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            using var _turn = BeginTurn(RunCaller(RunId));
            start.Wait(TimeSpan.FromSeconds(10));
            var results = new List<string>();
            for (var i = 0; i < 50; i++)
            {
                string result;
                try
                {
                    result = await InvokeAsync(tool);
                }
                catch (Exception ex)
                {
                    result = $"threw {ex.GetType().Name}: {ex.Message}"; // collected, so the assertions below name it
                }

                results.Add(result);
                if (!result.Contains(servedMarker, StringComparison.Ordinal))
                {
                    break; // refused: the removal has happened
                }

                Interlocked.Increment(ref served);
            }

            return results;
        })).ToArray();
        var removal = Task.Run(async () =>
        {
            start.Wait(TimeSpan.FromSeconds(10));
            // The assertion that at least one call reached the run before its removal: the removal waits for one.
            await UntilAsync(() => Volatile.Read(ref served) > 0, "a call to reach the run's server before the removal");
            await registry.OnRemovingAsync(Workspace(RunId), CancellationToken.None);
        });

        start.Set();
        await removal;
        var results = (await Task.WhenAll(calls)).SelectMany(r => r).ToList();

        var afterRemoval = await RoutedOrThrownAsync(tool);

        using var _scope = new AssertionScope();
        results.Should().OnlyContain(
            r => r.Contains(servedMarker, StringComparison.Ordinal)
                || r.StartsWith("error: run tool server 'roslyn' is not available for this run: ", StringComparison.Ordinal)
                || r.StartsWith("error: run tool server 'roslyn' stopped during 'args' for this run", StringComparison.Ordinal),
            "each call is served by its run, refused as not available, or reported as cut off; none throws");
        results.Should().NotContain(r => r.Contains("--host", StringComparison.Ordinal));
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

        var logged = CallLog.Lines(log);
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
        // Leak probe: every routed call disposed its lease, on every path, by the time its test returned.
        var outstanding = _disposables.OfType<RunMcpServerRegistry>().Sum(registry => registry.OutstandingLeases);
        for (var i = _disposables.Count - 1; i >= 0; i--)
        {
            await _disposables[i].DisposeAsync();
        }

        Directory.Delete(_root, recursive: true);
        outstanding.Should().Be(0, "no routed call leaves a lease behind");
    }

    private static RunScopedMcpDefinition RunScoped(params string[] extra) =>
        new() { Args = [McpServerFixture.ServerDll, "--run", "--id", "${run.id}", .. extra] };

    private RunMcpServerRegistry Registry(RunScopedMcpDefinition runScoped, TimeSpan? shutdownTimeout = null)
    {
        var definition = McpServerFixture.Definition("--host");
        definition.ShutdownTimeout = shutdownTimeout ?? definition.ShutdownTimeout;
        definition.RunScoped = runScoped;
        var registry = new RunMcpServerRegistry(
            new Dictionary<string, McpServerDefinition>(StringComparer.Ordinal) { ["roslyn"] = definition },
            () => new NoRecordsProvider(), NullLoggerFactory.Instance, TimeProvider.System);
        _disposables.Add(registry);
        return registry;
    }

    /// <summary>The routed tool <paramref name="toolName"/>, with a server started and ready for each run in <paramref name="ready"/>.</summary>
    private async Task<(AIFunction Tool, RunMcpServerRegistry Registry)> RoutedToolAsync(
        string toolName, RunScopedMcpDefinition? runScoped = null, Guid[]? ready = null, TimeSpan? shutdownTimeout = null, TimeProvider? clock = null)
    {
        runScoped ??= RunScoped();
        var registry = Registry(runScoped, shutdownTimeout);
        var source = Source(runScoped, registry, clock);

        foreach (var runId in ready ?? [])
        {
            await registry.OnReadyAsync(Workspace(runId), CancellationToken.None);
            var waited = await registry.WaitAllReadyAsync(runId, McpServerFixture.StartupBudget, CancellationToken.None);
            waited.IsSuccess.Should().BeTrue(waited.IsFailure ? waited.Error.Message : "");
        }

        var tools = await source.GetToolsAsync(CancellationToken.None);
        tools.IsSuccess.Should().BeTrue(tools.IsFailure ? tools.Error.Message : "");
        return (tools.Value.OfType<AIFunction>().Where(t => string.Equals(t.Name, toolName, StringComparison.Ordinal)).Should().ContainSingle().Subject, registry);
    }

    /// <summary>The routed source over <paramref name="registry"/>.</summary>
    /// <param name="runScoped">The run-scoped definition the source routes by.</param>
    /// <param name="registry">The registry that hands out the run servers' leases.</param>
    /// <param name="clock">Times the source's ready wait and call timeout; the registry keeps the system clock.</param>
    private RunScopedMcpToolSource Source(RunScopedMcpDefinition runScoped, RunMcpServerRegistry registry, TimeProvider? clock = null)
    {
        var definition = McpServerFixture.Definition("--host");
        definition.RunScoped = runScoped;
        var source = new RunScopedMcpToolSource(
            new McpToolSource("roslyn", definition, NullLoggerFactory.Instance), runScoped, registry, clock ?? TimeProvider.System, NullLogger<RunScopedMcpToolSource>.Instance);
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

    /// <summary>The client of <see cref="RunId"/>'s server, taken through a lease that is released at once.</summary>
    private async Task<ModelContextProtocol.Client.McpClient> ClientAsync(RunMcpServerRegistry registry)
    {
        var lease = await registry.GetReadyClientAsync("roslyn", RunId, CancellationToken.None);
        lease.IsSuccess.Should().BeTrue(lease.IsFailure ? lease.Error.Message : "");
        await lease.Value.DisposeAsync();
        return lease.Value.Client;
    }

    /// <summary>Calls <paramref name="tool"/> as a caller of <see cref="RunId"/>.</summary>
    private async Task<string> RoutedAsync(AIFunction tool)
    {
        using var _turn = BeginTurn(RunCaller(RunId));
        return await InvokeAsync(tool);
    }

    /// <summary>Calls <paramref name="tool"/> as a caller of <see cref="RunId"/>; an exception becomes <c>threw</c> text, so an assertion names it.</summary>
    private async Task<string> RoutedOrThrownAsync(AIFunction tool)
    {
        try
        {
            return await RoutedAsync(tool);
        }
        catch (Exception ex)
        {
            return $"threw {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Kills the run's server from outside, as a crash would, and waits until its client's session has ended, so the
    /// registry can see it dead. Returns the killed process id.
    /// </summary>
    private async Task<int> KillAndWaitForSessionEndAsync(RunMcpServerRegistry registry)
    {
        var lease = await registry.GetReadyClientAsync("roslyn", RunId, CancellationToken.None);
        lease.IsSuccess.Should().BeTrue(lease.IsFailure ? lease.Error.Message : "");
        await using (lease.Value)
        {
            var pid = int.Parse(((TextContentBlock)(await lease.Value.Client.CallToolAsync("pid")).Content.Single()).Text, CultureInfo.InvariantCulture);
            await KillAsync(pid);
            await lease.Value.Client.Completion.WaitAsync(HangGuard);
            return pid;
        }
    }

    private static async Task KillAsync(int pid)
    {
        using var process = Process.GetProcessById(pid);
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(HangGuard);
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
    /// The outcome of <paramref name="call"/>, a routed call that waits on a reload held at its gate, once its ready wait
    /// is over: <paramref name="clock"/> is moved on until the call gives up. The call's timer starts when the call
    /// does, which this test cannot observe, so the clock is moved on again until it has. <see cref="HangGuard"/> only
    /// stops the loop for a call that never gives up, which then fails here with a <see cref="TimeoutException"/>.
    /// </summary>
    private static async Task<string> PastTheReadyWaitAsync(Task<string> call, FakeTimeProvider clock)
    {
        var sw = Stopwatch.StartNew();
        while (!call.IsCompleted && sw.Elapsed < HangGuard)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.WhenAny(call, Task.Delay(50));
        }

        return await call.WaitAsync(TimeSpan.Zero); // throws TimeoutException if the call outlived the hang guard
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            sw.Elapsed.Should().BeLessThan(McpServerFixture.StartupBudget, $"waiting for {what}");
            await Task.Delay(50);
        }
    }

    private RunWorkspace Workspace(Guid runId) => new(runId, "repo", "https://example.invalid/repo.git", "main", "run/branch", _root, null);

    /// <summary>An argument whose serialization runs <paramref name="onWrite"/> once: a hook inside the routed call, before the request is sent.</summary>
    [JsonConverter(typeof(RunWhenSerializedConverter))]
    private sealed class RunWhenSerialized(Action onWrite)
    {
        private int _ran;

        public bool Ran => Volatile.Read(ref _ran) == 1;

        public void Run()
        {
            if (Interlocked.Exchange(ref _ran, 1) == 0)
            {
                onWrite();
            }
        }
    }

    private sealed class RunWhenSerializedConverter : JsonConverter<RunWhenSerialized>
    {
        public override RunWhenSerialized Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("only written");

        public override void Write(Utf8JsonWriter writer, RunWhenSerialized value, JsonSerializerOptions options)
        {
            value.Run();
            writer.WriteStringValue("hook");
        }
    }

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
