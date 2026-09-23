using Thalos;
using Thalos.Workflow;
using ZeroAlloc.Results;

namespace Thalos.Tests.Workflow;

/// <summary>
/// Tests the two properties <see cref="WorkflowNodeDispatcher"/> exists to get right: a node with declared
/// <see cref="ProcessNode.Outcomes"/> constrains the agent's result to that closed set through a tool call rather
/// than a prompt the model might paraphrase around, and a result outside that set fails the node instead of
/// silently picking a branch. Uses <see cref="FakeWorkflowStore"/> (no Postgres, no Docker) and
/// <see cref="FakeSubagentRunner"/> (no real agent dispatch) throughout — Task 4's own suite already proves the
/// real store's transactional behaviour, and dispatching a real subagent is explicitly out of scope here.
/// </summary>
public sealed class ConstrainedOutcomeTests : IAsyncLifetime
{
    private static readonly AgentId ReviewerId = AgentId.New();
    private static readonly AgentId StarterId = AgentId.New();
    private static readonly AgentId RelayerId = AgentId.New();

    /// <summary>
    /// <c>review</c> declares a closed outcome set and branches on it; <c>rework</c>/<c>done</c> are plain
    /// terminals. <c>agent:</c> is the reviewer's human-authored name — <see cref="FakeWorkflowReferenceResolver"/>
    /// is what turns it into an <see cref="AgentId"/>, mirroring how a real host resolves it over its own
    /// <c>IAgentCatalog</c>, never a raw id written into the process file.
    /// </summary>
    /// <remarks>
    /// Kept as YAML rather than a parsed <see cref="ProcessDefinition"/> because that is what a definition store
    /// holds: these fixtures are seeded into <see cref="InMemoryProcessDefinitionStore"/> and come back out
    /// through the same parse the real store performs, instead of being handed to the dispatcher pre-built.
    /// </remarks>
    private const string DefYaml = """
        process: gate-check
        version: 1
        nodes:
          review:
            agent: reviewer
            skill: code-review
            outcomes: [approved, rejected]
            branch: { approved: done, rejected: rework }
          rework: { terminal: failed }
          done: { terminal: succeeded }
        """;

    /// <summary>A task node feeding an approval gate feeding a terminal — the fixture the Critical fix needs and <see cref="DefYaml"/> never exercised.</summary>
    private const string GateDefYaml = """
        process: approval-flow
        version: 1
        nodes:
          start:
            agent: starter
            skill: kick-off
            next: gate
          gate: { await: human_approval, next: done }
          done: { terminal: succeeded }
        """;

    /// <summary>References an agent name no resolver entry covers, to test the unresolvable-agent path.</summary>
    private const string UnknownAgentDefYaml = """
        process: bad-agent
        version: 1
        nodes:
          only: { agent: ghost, skill: whatever, next: done }
          done: { terminal: succeeded }
        """;

    /// <summary>
    /// The loop-back primitive as an executable graph: <c>work</c> is capped at three entries and always branches
    /// to <c>relay</c>, which routes straight back to <c>work</c>. Two task nodes, not one task node and a bare
    /// relay, because <see cref="ProcessValidator"/> requires every node to be exactly one of task, gate or
    /// terminal — there is no pass-through node kind, so the cheapest possible loop still pays for a second
    /// agent turn per lap. <c>relay</c> declares no outcomes, so its turn reports nothing and its unconditional
    /// <c>next</c> is what re-enters the capped node and triggers the cap check.
    /// </summary>
    private const string CappedLoopDefYaml = """
        process: capped-loop
        version: 1
        nodes:
          work:
            agent: reviewer
            skill: code-review
            outcomes: [again, finish]
            branch: { again: relay, finish: shipped }
            maxVisits: 3
            onExceeded: exhausted
          relay: { agent: relayer, skill: kick-off, next: work }
          shipped: { terminal: succeeded }
          exhausted: { terminal: failed }
        """;

    private readonly FakeWorkflowStore _store;
    private readonly InMemoryProcessDefinitionStore _definitions;
    private readonly FakeSubagentRunner _runner;
    private readonly WorkflowNodeDispatcher _dispatcher;
    private Guid _runId;

    public ConstrainedOutcomeTests()
    {
        // The definitions live in the store and nowhere else — the dispatcher and the workflow store are both
        // handed the same instance, so there is no separate registry a test could seed differently from what a
        // sync would have written.
        _definitions = new InMemoryProcessDefinitionStore()
            .Seed(DefYaml)
            .Seed(GateDefYaml)
            .Seed(UnknownAgentDefYaml)
            .Seed(CappedLoopDefYaml);

        var resolver = new FakeWorkflowReferenceResolver(new Dictionary<string, AgentId>(StringComparer.Ordinal)
        {
            ["reviewer"] = ReviewerId,
            ["starter"] = StarterId,
            ["relayer"] = RelayerId,
        });

        _store = new FakeWorkflowStore(_definitions);
        _runner = new FakeSubagentRunner();
        _dispatcher = new WorkflowNodeDispatcher(_store, _runner, resolver, _definitions, _ => new FakeSecurityContext("workflow-engine"));
    }

    /// <summary>
    /// Starts the shared <c>gate-check</c> run through <see cref="IWorkflowStore.StartAsync"/> rather than seeding
    /// a row directly, so its first dispatch message is one the store produced. No test here builds that message
    /// itself: they take it off <see cref="FakeWorkflowStore.TakeNext"/>, which is the only shape in which a store
    /// that stopped enqueuing would make these tests visibly do nothing.
    /// </summary>
    public async Task InitializeAsync() =>
        _runId = await _store.StartAsync("gate-check", 1, "c-review", "review", initialVariables: null, CancellationToken.None);

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Takes the next message the store enqueued and dispatches it. Returns false when the outbox is empty —
    /// which, for a run that has parked or terminated, is the correct end of the line.
    /// </summary>
    private async Task<bool> DispatchNextAsync(Guid? runId = null)
    {
        if (_store.TakeNext(runId ?? _runId) is not { } message)
        {
            return false;
        }

        await _dispatcher.DispatchAsync(message, CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Dispatches until the outbox empties — which happens exactly when the run parks at a gate or reaches a
    /// terminal status, since those are the transitions that enqueue nothing. <paramref name="maxMessages"/> turns
    /// a genuinely non-terminating process into a loud failure instead of a hung test run.
    /// </summary>
    private async Task<int> DrainAsync(Guid? runId = null, int maxMessages = 32)
    {
        var dispatched = 0;
        while (await DispatchNextAsync(runId))
        {
            if (++dispatched > maxMessages)
            {
                throw new InvalidOperationException($"Dispatched more than {maxMessages} messages without the outbox emptying — the process under test does not terminate.");
            }
        }

        return dispatched;
    }

    /// <summary>
    /// Configures the fake runner to behave like a well-formed agent that calls the outcome tool with
    /// <paramref name="outcome"/>, and returns a handle exposing the <see cref="SubagentRunRequest"/> the
    /// dispatcher actually sent — this is the request-side evidence that a tool schema, not a prompt, carried the
    /// closed set.
    /// </summary>
    private CapturedInvocation CaptureAgentInvocation(string outcome = "approved")
    {
        var captured = new CapturedInvocation();
        _runner.NextResult = request =>
        {
            captured.Request = request;
            return Result<AgentTurnResult, AgentError>.Success(TurnResultReporting(outcome));
        };

        return captured;
    }

    /// <summary>
    /// Configures the fake runner to report <paramref name="outcome"/> via the outcome tool call, whatever its
    /// value — including one outside the node's declared set, to prove the dispatcher does not trust the model's
    /// tool call blindly.
    /// </summary>
    private void GivenAgentReturns(string outcome) =>
        _runner.NextResult = _ => Result<AgentTurnResult, AgentError>.Success(TurnResultReporting(outcome));

    /// <summary>Configures the fake runner to call the outcome tool twice, with two different, disagreeing values.</summary>
    private void GivenAgentReturnsDisagreeingOutcomes(string first, string second) =>
        _runner.NextResult = _ => Result<AgentTurnResult, AgentError>.Success(TurnResultReportingBoth(first, second));

    /// <summary>Configures the fake runner to complete the turn without ever calling the outcome tool.</summary>
    private void GivenAgentNeverReportsAnOutcome() =>
        _runner.NextResult = _ => Result<AgentTurnResult, AgentError>.Success(
            new AgentTurnResult(TurnId.New(), SessionId.New(), "Looked into it.", TurnUsage.Empty("test-model"), [], TimeSpan.Zero));

    private static AgentTurnResult TurnResultReporting(string outcome) =>
        new(TurnId.New(), SessionId.New(), $"Reported outcome: {outcome}", TurnUsage.Empty("test-model"), [OutcomeCall(outcome)], TimeSpan.Zero);

    private static AgentTurnResult TurnResultReportingBoth(string first, string second) =>
        new(TurnId.New(), SessionId.New(), "ambiguous", TurnUsage.Empty("test-model"), [OutcomeCall(first), OutcomeCall(second)], TimeSpan.Zero);

    private static ToolCallSummary OutcomeCall(string outcome) =>
        new(ToolCallId.New(), WorkflowNodeDispatcher.OutcomeToolName, $$"""{"outcome":"{{outcome}}"}""", true, outcome, TimeSpan.Zero);

    private sealed class CapturedInvocation
    {
        public SubagentRunRequest? Request { get; set; }
        public OutcomeToolSchema? OutcomeToolSchema => Request?.RequiredOutcome;
    }

    [Fact]
    public async Task A_node_with_declared_outcomes_constrains_the_agent_to_that_closed_set()
    {
        var captured = CaptureAgentInvocation();

        await DispatchNextAsync();

        captured.OutcomeToolSchema.Should().NotBeNull("the outcome must be a tool call, not parsed prose");
        captured.OutcomeToolSchema!.AllowedValues.Should().BeEquivalentTo(["approved", "rejected"]);
    }

    [Fact]
    public async Task A_free_text_outcome_fails_the_node_rather_than_guessing_a_branch()
    {
        GivenAgentReturns("approved, with some concerns");

        await DispatchNextAsync();

        var run = await _store.FindAsync(_runId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Failed);
        run.LastError.Should().Contain("approved, with some concerns").And.Contain("review");
    }

    /// <summary>
    /// Critical fix: an approval gate has no agent — <see cref="ProcessValidator"/>'s "exactly one of task, gate
    /// or terminal" rule guarantees it — so arriving at one must park the run at <see cref="WorkflowStatus.Awaiting"/>
    /// via <see cref="WorkflowInterpreter.Advance"/>, exactly like the terminal-node case, rather than trying
    /// (and failing) to resolve an agent that was never going to be there. Before the fix,
    /// <see cref="WorkflowNodeDispatcher"/> special-cased only <c>Terminal</c> and fell through to agent
    /// resolution for a gate, failing every run that ever reached one.
    /// </summary>
    [Fact]
    public async Task A_gate_arrival_parks_the_run_at_awaiting_instead_of_failing()
    {
        GivenAgentReturns("anything");  // 'start' declares no outcomes, so the reported value is ignored
        var gateRunId = await _store.StartAsync("approval-flow", 1, "c-gate", "start", initialVariables: null, CancellationToken.None);

        // Two messages, both produced by the store: StartAsync's dispatch for 'start', and the one completing
        // 'start' enqueues for 'gate'. The drain stops on its own once the gate parks, because a transition to
        // Awaiting enqueues nothing.
        var dispatched = await DrainAsync(gateRunId);

        dispatched.Should().Be(2, "StartAsync enqueues 'start', and completing 'start' enqueues 'gate'");
        var run = await _store.FindAsync(gateRunId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Awaiting);
        run.AwaitingSignal.Should().Be("human_approval");
        run.CurrentNode.Should().Be("gate");
        _runner.CallCount.Should().Be(1, "'start' runs its agent; the gate has none and parks on Advance's own verdict alone");
    }

    /// <summary>Important fix: <c>agent:</c> is a human-authored name resolved through <see cref="IWorkflowReferenceResolver"/>, not a raw <see cref="AgentId"/>.</summary>
    [Fact]
    public async Task An_unresolvable_agent_name_fails_the_node_instead_of_throwing()
    {
        var badRunId = await _store.StartAsync("bad-agent", 1, "c-bad-agent", "only", initialVariables: null, CancellationToken.None);

        (await DispatchNextAsync(badRunId)).Should().BeTrue("StartAsync must enqueue the start node's dispatch");

        var run = await _store.FindAsync(badRunId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Failed);
        run.LastError.Should().Contain("ghost").And.Contain("only");
        _runner.CallCount.Should().Be(0);
    }

    /// <summary>Cheap fix: the message's own node name is part of the contract — a mismatch against the run's actual position is corruption, not an ordinary redelivery race, and must fail loudly rather than act on the wrong node.</summary>
    [Fact]
    public async Task A_message_naming_the_wrong_node_fails_the_run_instead_of_acting_on_it()
    {
        // The real first message, corrupted in exactly one field — the node name — so the run's own seq still
        // matches and the node-mismatch guard, not the seq guard, is what has to catch this.
        var corrupted = _store.TakeNext(_runId)! with { Node = "done" };

        await _dispatcher.DispatchAsync(corrupted, CancellationToken.None);

        var run = await _store.FindAsync(_runId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Failed);
        run.LastError.Should().Contain("done").And.Contain("review");
        _runner.CallCount.Should().Be(0);
    }

    /// <summary>Cheap fix: two tool calls that disagree on the value must fail the node — picking either one would be exactly the guess this task exists to eliminate.</summary>
    [Fact]
    public async Task Disagreeing_outcome_tool_calls_fail_the_node_instead_of_picking_one()
    {
        GivenAgentReturnsDisagreeingOutcomes("approved", "rejected");

        await DispatchNextAsync();

        var run = await _store.FindAsync(_runId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Failed);
        run.LastError.Should().Contain("approved").And.Contain("rejected").And.Contain("review");
    }

    /// <summary>Cheap fix: a turn that never calls the outcome tool gets its own explicit message, distinguishable in the event log from a call that reported a wrong value.</summary>
    [Fact]
    public async Task A_turn_that_never_calls_the_outcome_tool_fails_with_a_distinct_message()
    {
        GivenAgentNeverReportsAnOutcome();

        await DispatchNextAsync();

        var run = await _store.FindAsync(_runId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Failed);
        run.LastError.Should().Contain("review").And.Contain("without calling").And.Contain(WorkflowNodeDispatcher.OutcomeToolName);
    }

    [Fact]
    public async Task A_valid_outcome_advances_the_run_along_the_matching_branch()
    {
        GivenAgentReturns("approved");

        await DispatchNextAsync(); // review -[approved]-> done (Running), which enqueues 'done'
        var afterBranch = await _store.FindAsync(_runId, CancellationToken.None);
        afterBranch!.CurrentNode.Should().Be("done");
        afterBranch.Status.Should().Be(WorkflowStatus.Running);

        await DispatchNextAsync(); // done has no agent — Advance alone settles its terminal status
        var run = await _store.FindAsync(_runId, CancellationToken.None);
        run!.CurrentNode.Should().Be("done");
        run.Status.Should().Be(WorkflowStatus.Succeeded);
        _runner.CallCount.Should().Be(1, "the terminal node itself never runs an agent turn");
        _store.OutboxCount.Should().Be(0, "a terminal transition enqueues nothing");
    }

    [Fact]
    public async Task A_rejected_outcome_advances_the_run_along_its_own_branch()
    {
        GivenAgentReturns("rejected");

        var dispatched = await DrainAsync(); // review -[rejected]-> rework, then rework settles Failed

        dispatched.Should().Be(2, "one dispatch for 'review' and one for the 'rework' arrival it enqueued");
        var run = await _store.FindAsync(_runId, CancellationToken.None);
        run!.CurrentNode.Should().Be("rework");
        run.Status.Should().Be(WorkflowStatus.Failed);
        _runner.CallCount.Should().Be(1, "the terminal node itself never runs an agent turn");
    }

    /// <summary>
    /// The loop-back primitive driven end to end, with the counts coming from the store's own visit increments
    /// rather than from a hand-built <see cref="WorkflowRun.Visits"/> dictionary. Every other cap test constructs
    /// the visit count it wants and calls <see cref="WorkflowInterpreter.Advance"/> once, which proves the
    /// comparison and nothing about whether a run actually going round a loop produces those counts — and this is
    /// the primitive most able to burn money, so "runs exactly three times" needs to be a fact about the system,
    /// not about a trace someone wrote down. Turns red if the store stops incrementing on entry, if
    /// <c>ApplyCap</c> stops checking the resolved target, or if the comparison drifts by one in either
    /// direction: a cap that fired a lap early would leave <c>reviewerTurns</c> at two, one late at four.
    /// </summary>
    [Fact]
    public async Task A_capped_loop_runs_its_node_exactly_maxVisits_times_and_then_takes_onExceeded()
    {
        var reviewerTurns = 0;
        _runner.NextResult = request =>
        {
            if (request.AgentId == ReviewerId)
            {
                reviewerTurns++;
            }

            // 'again' is the looping branch; 'relay' declares no outcomes, so the value is ignored on its turn.
            return Result<AgentTurnResult, AgentError>.Success(TurnResultReporting("again"));
        };

        var loopRunId = await _store.StartAsync("capped-loop", 1, "c-loop", "work", initialVariables: null, CancellationToken.None);

        await DrainAsync(loopRunId);

        reviewerTurns.Should().Be(3, "maxVisits: 3 bounds entries to 'work' at three, so its agent runs three times and never a fourth");
        var run = await _store.FindAsync(loopRunId, CancellationToken.None);
        run!.CurrentNode.Should().Be("exhausted", "the fourth entry is intercepted and redirected to the cap's onExceeded target");
        run.Status.Should().Be(WorkflowStatus.Failed, "'exhausted' is a terminal: failed node");
        run.Visits["work"].Should().Be(3, "the store's own increments are what the cap counted — not a value this test supplied");
    }

    /// <summary>
    /// The outbox is at-least-once, so a message can be redelivered after the run it names has already moved
    /// past it. <see cref="WorkflowDispatchMessage.Seq"/> exists specifically so this can be detected and dropped
    /// without acting on it twice.
    /// </summary>
    [Fact]
    public async Task A_redelivered_message_with_a_stale_seq_is_dropped_silently()
    {
        GivenAgentReturns("approved");

        // Keep the store's own first message so it can be re-delivered verbatim below. Dispatching a copy of a
        // message the store really produced is what makes this a redelivery rather than a hand-made stand-in.
        var firstMessage = _store.TakeNext(_runId)!;
        await _dispatcher.DispatchAsync(firstMessage, CancellationToken.None); // review -[approved]-> done
        await DispatchNextAsync();                                             // done settles Succeeded
        var completedRun = await _store.FindAsync(_runId, CancellationToken.None);
        completedRun!.Status.Should().Be(WorkflowStatus.Succeeded);
        _runner.CallCount.Should().Be(1);

        // Redelivery of that same first message after the run has long since moved past it.
        await _dispatcher.DispatchAsync(firstMessage, CancellationToken.None);

        _runner.CallCount.Should().Be(1, "a stale seq must be dropped before the subagent runner is ever called again");
        var run = await _store.FindAsync(_runId, CancellationToken.None);
        run.Should().BeEquivalentTo(completedRun, "a dropped redelivery must leave the run completely untouched");
    }

    /// <summary>
    /// Proves redelivery cannot duplicate an external side effect a node's turn might have performed (e.g.
    /// <c>git__create_branch</c>): dispatching the identical message twice must reach the subagent runner exactly
    /// once, because the second delivery is dropped by the seq check before any turn is attempted. See the task
    /// report for the separate finding on <c>git__create_branch</c>'s own idempotency contract, which this
    /// dispatcher-level guard does not depend on but which is still worth recording.
    /// </summary>
    [Fact]
    public async Task Dispatching_the_same_message_twice_reaches_the_subagent_runner_only_once()
    {
        GivenAgentReturns("approved");
        var message = _store.TakeNext(_runId)!;

        await _dispatcher.DispatchAsync(message, CancellationToken.None);
        await _dispatcher.DispatchAsync(message, CancellationToken.None);

        _runner.CallCount.Should().Be(1);
    }

    /// <summary>
    /// A turn that fails outright (no tool call reachable at all — <see cref="AgentErrorCode.ProviderError"/> and
    /// similar) is a result the dispatcher dislikes, not an infrastructure gap: it must record the run failed
    /// rather than throw and let the outbox retry the same failing turn eight times.
    /// </summary>
    [Fact]
    public async Task A_failed_turn_fails_the_run_instead_of_throwing()
    {
        _runner.NextResult = _ => Result<AgentTurnResult, AgentError>.Failure(AgentError.ProviderError("model unavailable"));

        await DispatchNextAsync();

        var run = await _store.FindAsync(_runId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Failed);
        run.LastError.Should().Contain("model unavailable");
    }
}
