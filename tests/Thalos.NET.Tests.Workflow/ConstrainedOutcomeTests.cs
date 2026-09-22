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

    /// <summary><c>review</c> declares a closed outcome set and branches on it; <c>rework</c>/<c>done</c> are plain terminals.</summary>
    private static readonly ProcessDefinition Def = ProcessLoader.Load($$"""
        process: gate-check
        version: 1
        nodes:
          review:
            agent: {{ReviewerId}}
            skill: code-review
            outcomes: [approved, rejected]
            branch: { approved: done, rejected: rework }
          rework: { terminal: failed }
          done: { terminal: succeeded }
        """).Value;

    private readonly FakeWorkflowStore _store;
    private readonly FakeSubagentRunner _runner;
    private readonly WorkflowNodeDispatcher _dispatcher;
    private readonly Guid _runId = Guid.NewGuid();

    public ConstrainedOutcomeTests()
    {
        var processes = new Dictionary<(string, int), ProcessDefinition> { [("gate-check", 1)] = Def };
        _store = new FakeWorkflowStore(processes);
        _runner = new FakeSubagentRunner();
        _dispatcher = new WorkflowNodeDispatcher(_store, _runner, processes, _ => new FakeSecurityContext("workflow-engine"));

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

    private static AgentTurnResult TurnResultReporting(string outcome)
    {
        var toolCall = new ToolCallSummary(
            ToolCallId.New(),
            WorkflowNodeDispatcher.OutcomeToolName,
            $$"""{"outcome":"{{outcome}}"}""",
            true,
            outcome,
            TimeSpan.Zero);

        return new AgentTurnResult(
            TurnId.New(),
            SessionId.New(),
            $"Reported outcome: {outcome}",
            TurnUsage.Empty("test-model"),
            [toolCall],
            TimeSpan.Zero);
    }

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
