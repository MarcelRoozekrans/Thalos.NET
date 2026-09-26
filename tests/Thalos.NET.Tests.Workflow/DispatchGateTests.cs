using Thalos;
using Thalos.Skills;
using Thalos.Workflow;
using ZeroAlloc.Results;

namespace Thalos.Tests.Workflow;

/// <summary>
/// <see cref="WorkflowNodeDispatcher"/>'s <see cref="IWorkflowDispatchGate"/> hook: every gate a host wires runs,
/// in registration order, immediately before a task node's agent turn; a failing gate fails the run and never
/// reaches <see cref="ISubagentRunner.RunAsync"/>; a gate node (<see cref="ProcessNode.Await"/> set) and a
/// terminal node are never gated, because neither spends a turn; and an unexpected exception or a cancelled token
/// out of a gate propagates exactly as one out of <see cref="ISubagentRunner.RunAsync"/> does — it is never turned
/// into a failed run.
/// </summary>
/// <remarks>
/// Uses <see cref="FakeWorkflowStore"/>, <see cref="FakeSubagentRunner"/> and <see cref="InMemoryProcessDefinitionStore"/>
/// for the same reasons <see cref="ConstrainedOutcomeTests"/> does: no Postgres, no Docker, and dispatching a real
/// subagent is out of scope. Every assertion names, in its own remarks, the production change that turns it red.
/// </remarks>
public sealed class DispatchGateTests
{
    private static readonly AgentId WorkerId = AgentId.New();

    /// <summary>One task node feeding a terminal — the minimum shape a task-node gate check needs.</summary>
    private const string GatedTaskDefYaml = """
        process: gated-task
        version: 1
        nodes:
          work:
            agent: worker
            skill: do-work
            next: done
          done: { terminal: succeeded }
        """;

    /// <summary>A lone approval gate feeding a terminal — a run can start positioned directly on it.</summary>
    private const string GateNodeDefYaml = """
        process: gate-only
        version: 1
        nodes:
          gate: { await: human_approval, next: done }
          done: { terminal: succeeded }
        """;

    /// <summary>A lone terminal — a run can start positioned directly on it too.</summary>
    private const string TerminalOnlyDefYaml = """
        process: terminal-only
        version: 1
        nodes:
          done: { terminal: succeeded }
        """;

    [Fact]
    public async Task A_failing_gate_fails_the_run_and_runs_no_turn()
    {
        var (dispatcher, runner, store, message) = await ArrangeTaskNodeAsync(
            [new FixedGate(Result.Failure("tool servers not ready within 00:10:00"))]);

        await dispatcher.DispatchAsync(message, CancellationToken.None);

        // Red if the gate loop moves to run after _runner.RunAsync instead of before it: the turn would already
        // have been dispatched by the time the (still failing) gate is checked.
        runner.Requests.Should().BeEmpty();

        var run = await store.FindAsync(message.RunId, CancellationToken.None);

        // Red if the failure branch returns without recording the failure - the FailAsync call dropped while the
        // early "return" stays - leaving the run Running instead of Failed.
        run!.Status.Should().Be(WorkflowStatus.Failed);

        // Red for the same reason as above, and also red if the "node '{node}': " prefix is dropped from the
        // message the dispatcher builds - Contain alone would let that pass, so this pins the full string.
        run.LastError.Should().Be("node 'work': tool servers not ready within 00:10:00");
    }

    [Fact]
    public async Task A_passing_gate_lets_the_turn_run()
    {
        var gate = new CapturingGate(Result.Success());
        var (dispatcher, runner, _, message) = await ArrangeTaskNodeAsync([gate]);

        await dispatcher.DispatchAsync(message, CancellationToken.None);

        // Red if a stray "return" is left after the gate loop, reachable even when every gate passed: the turn
        // would never run and this list would stay empty.
        runner.Requests.Should().ContainSingle();

        // Red if the gate is called with a literal node name instead of run.CurrentNode: this would read whatever
        // wrong constant replaced it instead of "work".
        gate.SeenNode.Should().Be("work");
    }

    [Fact]
    public async Task A_failing_gate_fails_a_pinned_run_and_runs_no_turn()
    {
        var pin = new NodePin("worker", WorkerId, "r1", "do-work", "h1");
        var (dispatcher, runner, store, message) = await ArrangePinnedTaskNodeAsync(
            [new FixedGate(Result.Failure("tool servers not ready within 00:10:00"))],
            new Dictionary<string, NodePin>(StringComparer.Ordinal) { ["work"] = pin });

        await dispatcher.DispatchAsync(message, CancellationToken.None);

        // Red if the gate loop moves to run after the "if (run.Manifest is { } manifest)" branch instead of
        // before it: a pinned run whose node IS named in the manifest - the shape every Daedalus run takes -
        // would then run RunPinnedNodeAsync and dispatch to the runner without any gate ever being checked.
        runner.Requests.Should().BeEmpty();
        var run = await store.FindAsync(message.RunId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Failed);
        run.LastError.Should().Be("node 'work': tool servers not ready within 00:10:00");
    }

    [Fact]
    public async Task A_failing_gate_is_reported_even_when_the_run_manifest_has_a_gap()
    {
        var (dispatcher, _, store, message) = await ArrangePinnedTaskNodeAsync(
            [new FixedGate(Result.Failure("tool servers not ready within 00:10:00"))],
            new Dictionary<string, NodePin>(StringComparer.Ordinal)); // gap: "work" is not named

        await dispatcher.DispatchAsync(message, CancellationToken.None);

        // Red if the gate loop moves to run after the "if (run.Manifest is { } manifest)" branch: the manifest
        // gap would then be checked first, before any gate ever runs, and this run would fail with "node 'work'
        // is missing from the run manifest." instead of the gate's own message - proving the gate genuinely runs
        // before the manifest branch, not merely before one arm of it. (Whether requests stayed empty would not
        // tell the two shapes apart here - the missing-node branch returns before reaching the runner either
        // way - so this message is the one assertion that does.)
        var run = await store.FindAsync(message.RunId, CancellationToken.None);
        run!.LastError.Should().Be("node 'work': tool servers not ready within 00:10:00");
    }

    [Fact]
    public async Task A_gate_node_is_never_gated()
    {
        var (dispatcher, store, message) = await ArrangeGateNodeAsync(
            [new FixedGate(Result.Failure("this must never be seen"))]);

        await dispatcher.DispatchAsync(message, CancellationToken.None);

        var run = await store.FindAsync(message.RunId, CancellationToken.None);

        // Red if the gate loop moves from DispatchTaskNodeAsync to the top of DispatchAsync, ahead of the
        // terminal/gate short-circuit: an approval gate would then be checked against the same failing dispatch
        // gate and the run would fail closed instead of parking.
        run!.Status.Should().Be(WorkflowStatus.Awaiting);
        run.AwaitingSignal.Should().Be("human_approval");

        // Same red as above: a run failed by the misplaced gate check carries the gate's error message here
        // instead of staying untouched.
        run.LastError.Should().BeNull();
    }

    [Fact]
    public async Task A_terminal_node_is_never_gated()
    {
        var (dispatcher, store, message) = await ArrangeTerminalNodeAsync(
            [new FixedGate(Result.Failure("this must never be seen"))]);

        await dispatcher.DispatchAsync(message, CancellationToken.None);

        var run = await store.FindAsync(message.RunId, CancellationToken.None);

        // Same misplaced-gate-loop red as the gate-node test above, on the other node kind Advance short-circuits
        // before ever reaching DispatchTaskNodeAsync.
        run!.Status.Should().Be(WorkflowStatus.Succeeded);
        run.LastError.Should().BeNull();
    }

    [Fact]
    public async Task A_second_gate_never_runs_once_an_earlier_one_has_failed()
    {
        var calls = new List<string>();
        var (dispatcher, runner, _, message) = await ArrangeTaskNodeAsync(
            [
                new RecordingGate("first", calls, Result.Failure("no")),
                new RecordingGate("second", calls, Result.Success()),
            ]);

        await dispatcher.DispatchAsync(message, CancellationToken.None);

        // Red if the loop keeps running every gate instead of returning on the first failure (for example,
        // deferring the FailAsync call until after the whole loop finishes): "second" would be recorded too, and
        // this list would hold both names instead of just the first.
        calls.Should().Equal("first");
        runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Every_passing_gate_runs_in_registration_order_before_the_turn()
    {
        var calls = new List<string>();
        var (dispatcher, runner, _, message) = await ArrangeTaskNodeAsync(
            [
                new RecordingGate("first", calls, Result.Success()),
                new RecordingGate("second", calls, Result.Success()),
            ]);

        await dispatcher.DispatchAsync(message, CancellationToken.None);

        // Red if the loop iterates _gates in reverse: "second" would be recorded ahead of "first".
        calls.Should().Equal("first", "second");

        // Red if a stray "return" is left after the gate loop, reachable even when every gate passed (the same
        // red the passing-gate test above names).
        runner.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task An_exception_from_a_gate_propagates_and_leaves_the_run_running()
    {
        var thrown = new InvalidOperationException("tool server registry unreachable");
        var (dispatcher, _, store, message) = await ArrangeTaskNodeAsync([new ThrowingGate(thrown)]);

        var dispatch = async () => await dispatcher.DispatchAsync(message, CancellationToken.None);

        // Red if the gate call is wrapped in a try/catch that reports the exception through FailAsync instead of
        // letting it propagate: dispatch would then complete normally instead of throwing. The BeSameAs half is
        // separately red if the dispatcher instead catches and rethrows a new exception of the same type (for
        // example wrapping it) rather than letting the original instance escape unchanged - ThrowExactlyAsync
        // alone would not catch that, since the type still matches.
        (await dispatch.Should().ThrowExactlyAsync<InvalidOperationException>()).Which.Should().BeSameAs(thrown);

        var run = await store.FindAsync(message.RunId, CancellationToken.None);

        // Same catch-and-report red as above: it would leave the run Failed instead of still Running for the
        // outbox to redeliver.
        run!.Status.Should().Be(WorkflowStatus.Running);
        run.LastError.Should().BeNull();
    }

    [Fact]
    public async Task A_gate_observing_cancellation_propagates_it_rather_than_failing_the_run()
    {
        var (dispatcher, _, store, message) = await ArrangeTaskNodeAsync([new DelayUntilCancelledGate()]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // DelayUntilCancelledGate awaits Task.Delay(Timeout.InfiniteTimeSpan, ct) using whatever token the
        // dispatcher actually hands it. cts.Token is already cancelled, so if that token reaches the gate,
        // Task.Delay throws immediately - this test runs in milliseconds. If the dispatcher instead passes
        // CancellationToken.None to the gate (the bug this test exists to catch), the gate's delay never
        // observes cts and would otherwise wait forever; the outer WaitAsync bounds that to two seconds so a
        // wrong implementation fails fast instead of hanging the suite.
        var dispatch = async () => await dispatcher.DispatchAsync(message, cts.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        // Red if the dispatcher passes CancellationToken.None (or any other token) to the gate instead of the
        // ct it itself received: see the setup above. A TimeoutException surfacing here, not an
        // OperationCanceledException, is exactly that red - ThrowAsync reports the mismatch as a clean
        // assertion failure rather than letting the TimeoutException escape uncaught.
        await dispatch.Should().ThrowAsync<OperationCanceledException>();

        var run = await store.FindAsync(message.RunId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Running);
        run.LastError.Should().BeNull();
    }

    // --- arrangement -----------------------------------------------------------------------------------------

    private static async Task<(WorkflowNodeDispatcher Dispatcher, FakeSubagentRunner Runner, FakeWorkflowStore Store, WorkflowDispatchMessage Message)> ArrangeTaskNodeAsync(
        IEnumerable<IWorkflowDispatchGate> gates)
    {
        var definitions = new InMemoryProcessDefinitionStore().Seed(GatedTaskDefYaml);
        var resolver = new FakeWorkflowReferenceResolver(new Dictionary<string, AgentId>(StringComparer.Ordinal) { ["worker"] = WorkerId });
        var store = new FakeWorkflowStore(definitions);
        var runner = new FakeSubagentRunner
        {
            NextResult = _ => Result<AgentTurnResult, AgentError>.Success(
                new AgentTurnResult(TurnId.New(), SessionId.New(), "done", TurnUsage.Empty("test-model"), [], TimeSpan.Zero)),
        };
        var dispatcher = new WorkflowNodeDispatcher(
            store, runner, resolver, definitions, new InMemorySkillStore(TimeProvider.System),
            _ => new FakeSecurityContext("workflow-engine"), gates);

        var runId = (await store.StartAsync(new WorkflowStartRequest { Process = "gated-task", Version = 1, CorrelationKey = "c-gated-task", StartNode = "work", InitialVariables = null, StartedBy = TestPrincipals.Starter }, CancellationToken.None)).Value;
        return (dispatcher, runner, store, store.TakeNext(runId)!);
    }

    /// <summary>
    /// The same single-task-node process as <see cref="ArrangeTaskNodeAsync"/>, but started with
    /// <paramref name="manifestNodes"/> as the run's <see cref="RunManifest"/> - so a gate is exercised on the
    /// pinned path Daedalus actually uses in production, not only on the unpinned, live-resolution path every
    /// other test in this file uses. The resolver is empty on purpose, exactly as <c>RunPinningDispatchTests</c>
    /// keeps it: a pinned node must never resolve its agent name live, so a resolver call here would itself be a
    /// sign this test took the wrong path.
    /// </summary>
    private static async Task<(WorkflowNodeDispatcher Dispatcher, FakeSubagentRunner Runner, FakeWorkflowStore Store, WorkflowDispatchMessage Message)> ArrangePinnedTaskNodeAsync(
        IEnumerable<IWorkflowDispatchGate> gates, IReadOnlyDictionary<string, NodePin> manifestNodes)
    {
        var definitions = new InMemoryProcessDefinitionStore().Seed(GatedTaskDefYaml);
        var resolver = new FakeWorkflowReferenceResolver(new Dictionary<string, AgentId>(StringComparer.Ordinal));
        var store = new FakeWorkflowStore(definitions);
        var runner = new FakeSubagentRunner
        {
            NextResult = _ => Result<AgentTurnResult, AgentError>.Success(
                new AgentTurnResult(TurnId.New(), SessionId.New(), "done", TurnUsage.Empty("test-model"), [], TimeSpan.Zero)),
        };
        var skills = new InMemorySkillStore(TimeProvider.System);
        await skills.UpsertAsync(new SkillDocument
        {
            Name = SkillName.Parse("do-work"),
            Description = "a test skill",
            Body = "Pinned body.",
            SourcePath = "do-work/SKILL.md",
            ContentHash = "h1",
            UpdatedAt = TimeProvider.System.GetUtcNow(),
        }, CancellationToken.None);

        var dispatcher = new WorkflowNodeDispatcher(
            store, runner, resolver, definitions, skills,
            _ => new FakeSecurityContext("workflow-engine"), gates);

        var manifest = new RunManifest { Nodes = manifestNodes };
        var runId = (await store.StartAsync(new WorkflowStartRequest { Process = "gated-task", Version = 1, CorrelationKey = "c-pinned-gated-task", StartNode = "work", InitialVariables = null, Manifest = manifest, StartedBy = TestPrincipals.Starter }, CancellationToken.None)).Value;
        return (dispatcher, runner, store, store.TakeNext(runId)!);
    }

    private static async Task<(WorkflowNodeDispatcher Dispatcher, FakeWorkflowStore Store, WorkflowDispatchMessage Message)> ArrangeGateNodeAsync(
        IEnumerable<IWorkflowDispatchGate> gates)
    {
        var definitions = new InMemoryProcessDefinitionStore().Seed(GateNodeDefYaml);
        // Empty on purpose: a gate node names no agent, so a resolver call here would itself be a sign this test
        // took the wrong path.
        var resolver = new FakeWorkflowReferenceResolver(new Dictionary<string, AgentId>(StringComparer.Ordinal));
        var store = new FakeWorkflowStore(definitions);
        // Left unconfigured on purpose: a gate node's dispatch must never reach RunAsync at all, so a call here
        // throws InvalidOperationException rather than silently succeeding.
        var runner = new FakeSubagentRunner();
        var dispatcher = new WorkflowNodeDispatcher(
            store, runner, resolver, definitions, new InMemorySkillStore(TimeProvider.System),
            _ => new FakeSecurityContext("workflow-engine"), gates);

        var runId = (await store.StartAsync(new WorkflowStartRequest { Process = "gate-only", Version = 1, CorrelationKey = "c-gate-only", StartNode = "gate", InitialVariables = null, StartedBy = TestPrincipals.Starter }, CancellationToken.None)).Value;
        return (dispatcher, store, store.TakeNext(runId)!);
    }

    private static async Task<(WorkflowNodeDispatcher Dispatcher, FakeWorkflowStore Store, WorkflowDispatchMessage Message)> ArrangeTerminalNodeAsync(
        IEnumerable<IWorkflowDispatchGate> gates)
    {
        var definitions = new InMemoryProcessDefinitionStore().Seed(TerminalOnlyDefYaml);
        var resolver = new FakeWorkflowReferenceResolver(new Dictionary<string, AgentId>(StringComparer.Ordinal));
        var store = new FakeWorkflowStore(definitions);
        // Left unconfigured on purpose, for the same reason as ArrangeGateNodeAsync: a terminal node's dispatch
        // must never reach RunAsync either.
        var runner = new FakeSubagentRunner();
        var dispatcher = new WorkflowNodeDispatcher(
            store, runner, resolver, definitions, new InMemorySkillStore(TimeProvider.System),
            _ => new FakeSecurityContext("workflow-engine"), gates);

        var runId = (await store.StartAsync(new WorkflowStartRequest { Process = "terminal-only", Version = 1, CorrelationKey = "c-terminal-only", StartNode = "done", InitialVariables = null, StartedBy = TestPrincipals.Starter }, CancellationToken.None)).Value;
        return (dispatcher, store, store.TakeNext(runId)!);
    }

    // --- test doubles ------------------------------------------------------------------------------------------

    /// <summary>Always returns the same pre-configured <see cref="Result"/>, for a test that only cares which branch fires.</summary>
    private sealed class FixedGate(Result result) : IWorkflowDispatchGate
    {
        public ValueTask<Result> BeforeTaskNodeAsync(WorkflowRun run, string node, CancellationToken ct) =>
            ValueTask.FromResult(result);
    }

    /// <summary>Records the node it was called with, alongside the fixed result it returns — lets a test read back exactly what the dispatcher passed it.</summary>
    private sealed class CapturingGate(Result result) : IWorkflowDispatchGate
    {
        public string? SeenNode { get; private set; }

        public ValueTask<Result> BeforeTaskNodeAsync(WorkflowRun run, string node, CancellationToken ct)
        {
            SeenNode = node;
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Appends <paramref name="name"/> to a call log shared across every gate in a chain, then returns <paramref name="result"/> — lets a test see whether, and in what order, each gate in the chain was actually reached.</summary>
    private sealed class RecordingGate(string name, List<string> calls, Result result) : IWorkflowDispatchGate
    {
        public ValueTask<Result> BeforeTaskNodeAsync(WorkflowRun run, string node, CancellationToken ct)
        {
            calls.Add(name);
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Throws <paramref name="exception"/> synchronously — stands in for a gate whose own dependency (for example a tool-server wait) failed unexpectedly, rather than returning a considered failure.</summary>
    private sealed class ThrowingGate(Exception exception) : IWorkflowDispatchGate
    {
        public ValueTask<Result> BeforeTaskNodeAsync(WorkflowRun run, string node, CancellationToken ct) =>
            throw exception;
    }

    /// <summary>
    /// Waits on the <paramref name="ct"/> it is actually called with, forever, until that token is cancelled —
    /// stands in for a gate whose own wait (for example <c>IRunToolServerReadiness.WaitAllReadyAsync</c>) genuinely
    /// honours the dispatch token, so a test using this gate can tell "the dispatcher forwarded its real ct to the
    /// gate" apart from "the dispatcher passed some other token", which a gate that ignores <paramref name="ct"/>
    /// entirely (like <see cref="ThrowingGate"/> constructed with a bare <see cref="OperationCanceledException"/>)
    /// cannot.
    /// </summary>
    private sealed class DelayUntilCancelledGate : IWorkflowDispatchGate
    {
        public async ValueTask<Result> BeforeTaskNodeAsync(WorkflowRun run, string node, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Result.Success();
        }
    }
}
