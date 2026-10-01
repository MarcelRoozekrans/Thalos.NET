using System.Globalization;
using Thalos;
using Thalos.Skills;
using Thalos.Workflow;
using ZeroAlloc.Results;

namespace Thalos.Tests.Workflow;

/// <summary>
/// <see cref="WorkflowNodeDispatcher"/> dispatching an action node (<see cref="ProcessNode.Action"/> set): it runs
/// the <see cref="IWorkflowHostAction"/> registered under that name instead of an agent turn, advances on the
/// outcome and variables the action reports, fails the run for an unregistered action, a failed result, variables
/// past the key caps and an undeclared outcome, and lets an exception or a cancelled token propagate for the outbox,
/// exactly as a task node's turn does. No dispatch gate runs before an action.
/// </summary>
/// <remarks>
/// Uses <see cref="FakeWorkflowStore"/>, <see cref="FakeSubagentRunner"/> and <see cref="InMemoryProcessDefinitionStore"/>
/// for the same reasons <see cref="ConstrainedOutcomeTests"/> does. Every assertion names, in its own comment, the
/// production change that turns it red.
/// </remarks>
public sealed class HostActionDispatchTests
{
    private const string ActionName = "open-pull-request";

    /// <summary>An action node that branches on its outcome, the shape the docs show.</summary>
    private const string BranchingActionYaml = """
        process: publishing
        version: 1
        nodes:
          publish:
            action: open-pull-request
            outcomes: [published, failed]
            branch: { published: done, failed: stop }
          done: { terminal: succeeded }
          stop: { terminal: failed }
        """;

    /// <summary>The same node leaving by <c>next</c>, its declared outcomes informational.</summary>
    private const string NextActionYaml = """
        process: publishing
        version: 1
        nodes:
          publish:
            action: open-pull-request
            outcomes: [published, failed]
            next: done
          done: { terminal: succeeded }
        """;

    [Theory]
    [InlineData("published", "done")]
    [InlineData("failed", "stop")]
    public async Task An_action_node_runs_the_action_and_branches_on_its_outcome_with_its_variables(string outcome, string expectedNode)
    {
        var action = RecordingAction.Returning(ActionName, new HostActionResult(outcome, Vars(("pr_url", "https://x/1"))));
        var (dispatcher, store, message) = await ArrangeAsync(BranchingActionYaml, [action], pinned: true);

        await dispatcher.DispatchAsync(message, CancellationToken.None);

        // Red if action nodes are routed into DispatchTaskNodeAsync: the action is never called.
        action.Calls.Should().Be(1);

        var run = await store.FindAsync(message.RunId, CancellationToken.None);

        // Red, on the "published" row, if action nodes are routed into DispatchTaskNodeAsync: this pinned run, whose
        // manifest does not name the action node, fails with "missing from the run manifest" and stays at "publish".
        // Red, on the "failed" row, if the dispatcher advances on the node's first declared outcome instead of the
        // one the action reported: the run goes to "done" instead of "stop".
        run!.CurrentNode.Should().Be(expectedNode);

        // Red if the dispatcher advances with an empty variable bag instead of the action's variables.
        run.Variables.Should().ContainKey("pr_url").WhoseValue.Should().Be("https://x/1");
    }

    [Fact]
    public async Task An_action_failure_fails_the_run_with_its_message()
    {
        var action = RecordingAction.Failing(ActionName, "push failed; worktree kept at C:/w");
        var (dispatcher, store, message) = await ArrangeAsync(BranchingActionYaml, [action]);

        await dispatcher.DispatchAsync(message, CancellationToken.None);

        var run = await store.FindAsync(message.RunId, CancellationToken.None);

        // Red if a failed result is turned into the node's "failed" outcome instead of failing the run: the run
        // would branch to "stop" and still be Running.
        run!.Status.Should().Be(WorkflowStatus.Failed);

        // Same red as above, which leaves LastError null, and red if the "node 'publish': " prefix is dropped.
        run.LastError.Should().Be("node 'publish': push failed; worktree kept at C:/w");
    }

    [Fact]
    public async Task A_retried_action_node_runs_the_action_again_and_advances()
    {
        var action = RecordingAction.FailingThenReturning(ActionName, "push failed", new HostActionResult("published", Vars(("pr_url", "https://x/1"))));
        var (dispatcher, store, message) = await ArrangeAsync(NextActionYaml, [action]);

        await dispatcher.DispatchAsync(message, CancellationToken.None);
        var failed = await store.FindAsync(message.RunId, CancellationToken.None);

        // Red if FailingThenReturning never fails its first call: the run is Succeeded here.
        failed!.Status.Should().Be(WorkflowStatus.Failed);

        var retried = await store.RetryFailedNodeAsync(message.RunId, new WorkflowRetryRequest { ExpectedSeq = failed.CurrentSeq, RetriedBy = new RunPrincipal("op", ["admin"]) }, CancellationToken.None);

        // Red if the fake's retry refuses a Failed action-node run.
        retried.IsSuccess.Should().BeTrue(retried.IsFailure ? retried.Error : "");

        // Red if the fake's retry enqueues nothing: TakeNext returns null.
        var next = store.TakeNext(message.RunId);
        next.Should().NotBeNull();
        await dispatcher.DispatchAsync(next!, CancellationToken.None);

        // Red if the retry dispatches at a seq the dispatcher drops: the action runs once, not twice.
        action.Calls.Should().Be(2);
        var run = await store.FindAsync(message.RunId, CancellationToken.None);

        // Red for the same reason: the run stays at publish.
        run!.CurrentNode.Should().Be("done");

        // Red if the retried dispatch advances no further: there is no message for the terminal node to take.
        var terminal = store.TakeNext(message.RunId);
        terminal.Should().NotBeNull();
        await dispatcher.DispatchAsync(terminal!, CancellationToken.None);

        // Red if the retry leaves the run Failed, or the terminal node is not reached: the run is not Succeeded.
        (await store.FindAsync(message.RunId, CancellationToken.None))!.Status.Should().Be(WorkflowStatus.Succeeded);
    }

    [Fact]
    public async Task An_unregistered_action_fails_the_run_at_dispatch()
    {
        // Registered under the same name in a different case: IWorkflowHostAction.Name is compared ordinally.
        var differentCase = RecordingAction.Returning("Open-Pull-Request", new HostActionResult("published", Vars()));
        var (dispatcher, store, message) = await ArrangeAsync(BranchingActionYaml, [differentCase]);

        var dispatch = async () => await dispatcher.DispatchAsync(message, CancellationToken.None);

        // Red if the lookup indexes the dictionary directly instead of TryGetValue: KeyNotFoundException escapes.
        await dispatch.Should().NotThrowAsync();

        // Red if HostActionIndex compares names with OrdinalIgnoreCase: the differently cased action would run.
        differentCase.Calls.Should().Be(0);

        var run = await store.FindAsync(message.RunId, CancellationToken.None);

        // Red if the missing-action branch returns without calling FailAsync: the run stays Running.
        run!.Status.Should().Be(WorkflowStatus.Failed);
        run.LastError.Should().Be("node 'publish' references host action 'open-pull-request', which is not registered.");
    }

    [Fact]
    public async Task An_action_exception_propagates_for_the_outbox_to_retry()
    {
        var thrown = new IOException("remote unreachable");
        var (dispatcher, store, message) = await ArrangeAsync(BranchingActionYaml, [RecordingAction.Throwing(ActionName, thrown)]);

        var dispatch = async () => await dispatcher.DispatchAsync(message, CancellationToken.None);

        // Red if the action call is wrapped in a try/catch that reports the exception through FailAsync: nothing
        // is thrown. BeSameAs is separately red if the dispatcher wraps it in a new exception of the same type.
        (await dispatch.Should().ThrowExactlyAsync<IOException>()).Which.Should().BeSameAs(thrown);

        var run = await store.FindAsync(message.RunId, CancellationToken.None);

        // Same catch-and-report red: the run would be Failed with the exception's message.
        run!.Status.Should().Be(WorkflowStatus.Running);
        run.LastError.Should().BeNull();

        // Red if a catch advances the run on the node's "failed" outcome instead: the seq would move on.
        run.CurrentSeq.Should().Be(message.Seq);
    }

    [Fact]
    public async Task An_action_observing_cancellation_propagates_it_rather_than_failing_the_run()
    {
        var (dispatcher, store, message) = await ArrangeAsync(BranchingActionYaml, [RecordingAction.WaitingForCancellation(ActionName)]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // The action waits on whatever token it is handed. If that is cts.Token, the wait ends at once; if the
        // dispatcher hands it another token, the wait never ends, and WaitAsync turns that into a TimeoutException
        // after two seconds rather than hanging the suite.
        var dispatch = async () => await dispatcher.DispatchAsync(message, cts.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        // Red if the dispatcher passes CancellationToken.None to the action: a TimeoutException surfaces instead.
        // Also red if the action call is wrapped in a catch-all that fails the run: nothing is thrown.
        await dispatch.Should().ThrowAsync<OperationCanceledException>();

        var run = await store.FindAsync(message.RunId, CancellationToken.None);

        // Red under the catch-all above: the run would be Failed.
        run!.Status.Should().Be(WorkflowStatus.Running);
        run.LastError.Should().BeNull();
    }

    [Theory]
    [InlineData(NextActionYaml)]
    [InlineData(BranchingActionYaml)]
    public async Task An_undeclared_outcome_fails_the_run_whichever_edge_the_node_leaves_by(string yaml)
    {
        var action = RecordingAction.Returning(ActionName, new HostActionResult("merged", Vars()));
        var (dispatcher, store, message) = await ArrangeAsync(yaml, [action]);

        await dispatcher.DispatchAsync(message, CancellationToken.None);

        var run = await store.FindAsync(message.RunId, CancellationToken.None);

        // Red, on the next row, if WorkflowInterpreter.Advance checks declared outcomes only when the node has a
        // branch: the run takes 'next' to "done" and stays Running.
        run!.Status.Should().Be(WorkflowStatus.Failed);

        // Red, on the branch row, if Advance drops the declared-outcome check altogether: the run still fails, but
        // with "has no branch destination for outcome 'merged'" instead of this message.
        run.LastError.Should().Be("node 'publish' produced outcome 'merged' which is not one of its declared outcomes (published, failed)");
    }

    [Fact]
    public async Task An_action_reporting_more_variables_than_one_node_may_contribute_fails_the_run()
    {
        var tooMany = Enumerable.Range(0, WorkflowVariableBlock.MaxVariablesPerReport + 1)
            .Select(i => ("k" + i.ToString(CultureInfo.InvariantCulture), (object?)"v"))
            .ToArray();
        var action = RecordingAction.Returning(ActionName, new HostActionResult("published", Vars(tooMany)));
        var (dispatcher, store, message) = await ArrangeAsync(BranchingActionYaml, [action]);

        await dispatcher.DispatchAsync(message, CancellationToken.None);

        var run = await store.FindAsync(message.RunId, CancellationToken.None);

        // Red if DispatchActionNodeAsync skips CheckKeyLimits: the run advances to "done" with every variable.
        run!.Status.Should().Be(WorkflowStatus.Failed);
        run.LastError.Should().StartWith($"node 'publish' reported {WorkflowVariableBlock.MaxVariablesPerReport + 1} variables in one turn");
    }

    [Theory]
    [InlineData("result")]
    [InlineData("variables")]
    public async Task A_success_with_a_null_result_or_variable_bag_fails_the_run_instead_of_throwing(string missing)
    {
        var action = new RecordingAction(ActionName, _ => ValueTask.FromResult(Result<HostActionResult>.Success(
            string.Equals(missing, "result", StringComparison.Ordinal) ? null! : new HostActionResult("published", null!))));
        var (dispatcher, store, message) = await ArrangeAsync(BranchingActionYaml, [action]);

        var dispatch = async () => await dispatcher.DispatchAsync(message, CancellationToken.None);

        // Red, per row, if the null guard is removed: a NullReferenceException escapes for the outbox, which would
        // re-run the action on every retry.
        await dispatch.Should().NotThrowAsync();

        var run = await store.FindAsync(message.RunId, CancellationToken.None);

        // Red if the guard returns without calling FailAsync: the run stays Running.
        run!.Status.Should().Be(WorkflowStatus.Failed);

        // Red if the message stops naming the node, the action or which part was missing.
        run.LastError.Should().Be($"node 'publish': host action 'open-pull-request' returned a success with no {missing}; an action with none to report returns an empty dictionary.");
    }

    [Fact]
    public async Task No_dispatch_gate_runs_before_an_action_node()
    {
        var gate = new CountingFailingGate();
        var action = RecordingAction.Returning(ActionName, new HostActionResult("published", Vars()));
        var (dispatcher, store, message) = await ArrangeAsync(BranchingActionYaml, [action], gates: [gate]);

        await dispatcher.DispatchAsync(message, CancellationToken.None);

        // Red if the gate loop runs ahead of the action branch, for example moved from DispatchTaskNodeAsync into
        // DispatchAsync or repeated in DispatchActionNodeAsync: the gate is called.
        gate.Calls.Should().Be(0);

        var run = await store.FindAsync(message.RunId, CancellationToken.None);

        // Same red: the failing gate fails the run at "publish" before the action runs.
        run!.CurrentNode.Should().Be("done");
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("blank")]
    [InlineData("null")]
    public void A_host_action_registration_that_cannot_be_dispatched_unambiguously_is_refused_at_construction(string shape)
    {
        IWorkflowHostAction[] actions = shape switch
        {
            "duplicate" =>
            [
                RecordingAction.Returning(ActionName, new HostActionResult("published", Vars())),
                RecordingAction.Returning(ActionName, new HostActionResult("failed", Vars())),
            ],
            "blank" => [RecordingAction.Returning("  ", new HostActionResult("published", Vars()))],
            _ => [null!],
        };
        var definitions = new InMemoryProcessDefinitionStore();

        var construct = () => new WorkflowNodeDispatcher(
            new FakeWorkflowStore(definitions), new FakeSubagentRunner(),
            new FakeWorkflowReferenceResolver(new Dictionary<string, AgentId>(StringComparer.Ordinal)),
            definitions, new InMemorySkillStore(TimeProvider.System), _ => new FakeSecurityContext("workflow-engine"),
            gates: [], hostActions: actions);

        // Red, per row, if HostActionIndex drops that row's guard: a duplicate is then kept last-wins with no
        // throw, a blank name is indexed with no throw, and a null entry throws NullReferenceException on .Name
        // instead of ArgumentException.
        construct.Should().ThrowExactly<ArgumentException>().WithParameterName("hostActions");
    }

    // --- arrangement -----------------------------------------------------------------------------------------

    /// <summary>
    /// Seeds <paramref name="yaml"/>, starts a run at <c>publish</c> and returns the undispatched message. With
    /// <paramref name="pinned"/> the run carries a manifest that, as <c>CatalogRunManifestResolver</c> leaves it for
    /// an action node, does not name <c>publish</c>. The runner is configured to succeed, so a misrouted action node
    /// reaches a real failure branch instead of <see cref="FakeSubagentRunner"/>'s own guard.
    /// </summary>
    private static async Task<(WorkflowNodeDispatcher Dispatcher, FakeWorkflowStore Store, WorkflowDispatchMessage Message)> ArrangeAsync(
        string yaml, IEnumerable<IWorkflowHostAction> actions, bool pinned = false, IEnumerable<IWorkflowDispatchGate>? gates = null)
    {
        var definitions = new InMemoryProcessDefinitionStore().Seed(yaml);
        var store = new FakeWorkflowStore(definitions);
        var runner = new FakeSubagentRunner
        {
            NextResult = _ => Result<AgentTurnResult, AgentError>.Success(
                new AgentTurnResult(TurnId.New(), SessionId.New(), "done", TurnUsage.Empty("test-model"), [], TimeSpan.Zero)),
        };
        var dispatcher = new WorkflowNodeDispatcher(
            store, runner, new FakeWorkflowReferenceResolver(new Dictionary<string, AgentId>(StringComparer.Ordinal)),
            definitions, new InMemorySkillStore(TimeProvider.System), _ => new FakeSecurityContext("workflow-engine"),
            gates ?? [], actions);

        var manifest = pinned
            ? new RunManifest { Nodes = new Dictionary<string, NodePin>(StringComparer.Ordinal) }
            : null;
        var runId = (await store.StartAsync(new WorkflowStartRequest { Process = "publishing", Version = 1, CorrelationKey = "c-publishing", StartNode = "publish", InitialVariables = null, Manifest = manifest, StartedBy = TestPrincipals.Starter }, CancellationToken.None)).Value;
        return (dispatcher, store, store.TakeNext(runId)!);
    }

    private static Dictionary<string, object?> Vars(params (string Key, object? Value)[] entries) =>
        entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

    /// <summary>Fails every task node it is asked about, and counts how often it was asked.</summary>
    private sealed class CountingFailingGate : IWorkflowDispatchGate
    {
        public int Calls { get; private set; }

        public ValueTask<Result> BeforeTaskNodeAsync(WorkflowRun run, string node, CancellationToken ct)
        {
            Calls++;
            return ValueTask.FromResult(Result.Failure("this gate must never be asked about an action node"));
        }
    }
}
