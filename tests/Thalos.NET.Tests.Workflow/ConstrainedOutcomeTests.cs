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
public sealed class ConstrainedOutcomeTests
{
    private static readonly AgentId ReviewerId = AgentId.New();
    private static readonly AgentId StarterId = AgentId.New();

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

    private readonly FakeWorkflowStore _store;
    private readonly InMemoryProcessDefinitionStore _definitions;
    private readonly FakeSubagentRunner _runner;
    private readonly WorkflowNodeDispatcher _dispatcher;
    private readonly Guid _runId = Guid.NewGuid();

    public ConstrainedOutcomeTests()
    {
        // The definitions live in the store and nowhere else — the dispatcher and the workflow store are both
        // handed the same instance, so there is no separate registry a test could seed differently from what a
        // sync would have written.
        _definitions = new InMemoryProcessDefinitionStore()
            .Seed(DefYaml)
            .Seed(GateDefYaml)
            .Seed(UnknownAgentDefYaml);

        var resolver = new FakeWorkflowReferenceResolver(new Dictionary<string, AgentId>(StringComparer.Ordinal)
        {
            ["reviewer"] = ReviewerId,
            ["starter"] = StarterId,
        });

        _store = new FakeWorkflowStore(_definitions);
        _runner = new FakeSubagentRunner();
        _dispatcher = new WorkflowNodeDispatcher(_store, _runner, resolver, _definitions, _ => new FakeSecurityContext("workflow-engine"));

        _store.Seed(new WorkflowRun
        {
            Id = _runId,
            Process = "gate-check",
            ProcessVersion = 1,
            CurrentNode = "review",
            CurrentSeq = 1,
            Status = WorkflowStatus.Running,
            AwaitingSignal = null,
            Visits = new Dictionary<string, int>(StringComparer.Ordinal) { ["review"] = 1 },
        });
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

        await _dispatcher.DispatchAsync(new WorkflowDispatchMessage(_runId, 1, "review"), CancellationToken.None);

        captured.OutcomeToolSchema.Should().NotBeNull("the outcome must be a tool call, not parsed prose");
        captured.OutcomeToolSchema!.AllowedValues.Should().BeEquivalentTo(["approved", "rejected"]);
    }

    [Fact]
    public async Task A_free_text_outcome_fails_the_node_rather_than_guessing_a_branch()
    {
        GivenAgentReturns("approved, with some concerns");

        await _dispatcher.DispatchAsync(new WorkflowDispatchMessage(_runId, 1, "review"), CancellationToken.None);

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
        var gateRunId = Guid.NewGuid();
        _store.Seed(new WorkflowRun
        {
            Id = gateRunId,
            Process = "approval-flow",
            ProcessVersion = 1,
            CurrentNode = "gate",
            CurrentSeq = 1,
            Status = WorkflowStatus.Running,
            AwaitingSignal = null,
            Visits = new Dictionary<string, int>(StringComparer.Ordinal) { ["start"] = 1, ["gate"] = 1 },
        });

        await _dispatcher.DispatchAsync(new WorkflowDispatchMessage(gateRunId, 1, "gate"), CancellationToken.None);

        var run = await _store.FindAsync(gateRunId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Awaiting);
        run.AwaitingSignal.Should().Be("human_approval");
        run.CurrentNode.Should().Be("gate");
        _runner.CallCount.Should().Be(0, "a gate has no agent to run — it parks on Advance's own verdict alone");
    }

    /// <summary>Important fix: <c>agent:</c> is a human-authored name resolved through <see cref="IWorkflowReferenceResolver"/>, not a raw <see cref="AgentId"/>.</summary>
    [Fact]
    public async Task An_unresolvable_agent_name_fails_the_node_instead_of_throwing()
    {
        var badRunId = Guid.NewGuid();
        _store.Seed(new WorkflowRun
        {
            Id = badRunId,
            Process = "bad-agent",
            ProcessVersion = 1,
            CurrentNode = "only",
            CurrentSeq = 1,
            Status = WorkflowStatus.Running,
            AwaitingSignal = null,
            Visits = new Dictionary<string, int>(StringComparer.Ordinal) { ["only"] = 1 },
        });

        await _dispatcher.DispatchAsync(new WorkflowDispatchMessage(badRunId, 1, "only"), CancellationToken.None);

        var run = await _store.FindAsync(badRunId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Failed);
        run.LastError.Should().Contain("ghost").And.Contain("only");
        _runner.CallCount.Should().Be(0);
    }

    /// <summary>Cheap fix: the message's own node name is part of the contract — a mismatch against the run's actual position is corruption, not an ordinary redelivery race, and must fail loudly rather than act on the wrong node.</summary>
    [Fact]
    public async Task A_message_naming_the_wrong_node_fails_the_run_instead_of_acting_on_it()
    {
        await _dispatcher.DispatchAsync(new WorkflowDispatchMessage(_runId, 1, "done"), CancellationToken.None);

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

        await _dispatcher.DispatchAsync(new WorkflowDispatchMessage(_runId, 1, "review"), CancellationToken.None);

        var run = await _store.FindAsync(_runId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Failed);
        run.LastError.Should().Contain("approved").And.Contain("rejected").And.Contain("review");
    }

    /// <summary>Cheap fix: a turn that never calls the outcome tool gets its own explicit message, distinguishable in the event log from a call that reported a wrong value.</summary>
    [Fact]
    public async Task A_turn_that_never_calls_the_outcome_tool_fails_with_a_distinct_message()
    {
        GivenAgentNeverReportsAnOutcome();

        await _dispatcher.DispatchAsync(new WorkflowDispatchMessage(_runId, 1, "review"), CancellationToken.None);

        var run = await _store.FindAsync(_runId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Failed);
        run.LastError.Should().Contain("review").And.Contain("without calling").And.Contain(WorkflowNodeDispatcher.OutcomeToolName);
    }

    /// <summary>
    /// Dispatches whatever node <see cref="_runId"/> is currently positioned at, reading its live
    /// <c>CurrentSeq</c>/<c>CurrentNode</c> rather than a hard-coded one. A branch/next edge that lands on a
    /// terminal node reports <see cref="WorkflowStatus.Running"/>, not the terminal status itself — the same
    /// as production, where the store enqueues a fresh dispatch for that arrival — so reaching an actual
    /// terminal <see cref="WorkflowStatus"/> takes two calls to this helper: one for <c>review</c>, one for
    /// whichever terminal node it branched to.
    /// </summary>
    private async Task DispatchCurrentNodeAsync()
    {
        var run = await _store.FindAsync(_runId, CancellationToken.None);
        await _dispatcher.DispatchAsync(new WorkflowDispatchMessage(_runId, run!.CurrentSeq, run.CurrentNode), CancellationToken.None);
    }

    [Fact]
    public async Task A_valid_outcome_advances_the_run_along_the_matching_branch()
    {
        GivenAgentReturns("approved");

        await DispatchCurrentNodeAsync(); // review -[approved]-> done (Running)
        var afterBranch = await _store.FindAsync(_runId, CancellationToken.None);
        afterBranch!.CurrentNode.Should().Be("done");
        afterBranch.Status.Should().Be(WorkflowStatus.Running);

        await DispatchCurrentNodeAsync(); // done has no agent — Advance alone settles its terminal status
        var run = await _store.FindAsync(_runId, CancellationToken.None);
        run!.CurrentNode.Should().Be("done");
        run.Status.Should().Be(WorkflowStatus.Succeeded);
        _runner.CallCount.Should().Be(1, "the terminal node itself never runs an agent turn");
    }

    [Fact]
    public async Task A_rejected_outcome_advances_the_run_along_its_own_branch()
    {
        GivenAgentReturns("rejected");

        await DispatchCurrentNodeAsync(); // review -[rejected]-> rework (Running)
        await DispatchCurrentNodeAsync(); // rework has no agent — Advance alone settles its terminal status

        var run = await _store.FindAsync(_runId, CancellationToken.None);
        run!.CurrentNode.Should().Be("rework");
        run.Status.Should().Be(WorkflowStatus.Failed);
        _runner.CallCount.Should().Be(1, "the terminal node itself never runs an agent turn");
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
        await DispatchCurrentNodeAsync(); // review -[approved]-> done
        await DispatchCurrentNodeAsync(); // done settles Succeeded
        var completedRun = await _store.FindAsync(_runId, CancellationToken.None);
        completedRun!.Status.Should().Be(WorkflowStatus.Succeeded);
        _runner.CallCount.Should().Be(1);

        // Redelivery of the original message (seq 1, node "review") after the run has long since moved past it.
        await _dispatcher.DispatchAsync(new WorkflowDispatchMessage(_runId, 1, "review"), CancellationToken.None);

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
        var message = new WorkflowDispatchMessage(_runId, 1, "review");

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

        await _dispatcher.DispatchAsync(new WorkflowDispatchMessage(_runId, 1, "review"), CancellationToken.None);

        var run = await _store.FindAsync(_runId, CancellationToken.None);
        run!.Status.Should().Be(WorkflowStatus.Failed);
        run.LastError.Should().Contain("model unavailable");
    }
}
