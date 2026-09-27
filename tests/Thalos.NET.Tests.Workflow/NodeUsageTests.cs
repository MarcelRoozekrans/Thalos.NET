using Thalos;
using Thalos.Skills;
using Thalos.Workflow;
using ZeroAlloc.Results;

namespace Thalos.Tests.Workflow;

/// <summary>
/// A node's <see cref="AgentTurnResult.Usage"/> travels onto the <see cref="NodeResult"/>
/// <see cref="WorkflowNodeDispatcher"/> hands <see cref="IWorkflowStore.CompleteNodeAsync"/>, on both of
/// <c>BuildNodeResult</c>'s success paths: the outcome path a node with declared <c>outcomes</c> takes, and the
/// outcome-less path a node with none takes.
/// </summary>
/// <remarks>
/// Uses <see cref="FakeWorkflowStore"/> and <see cref="FakeSubagentRunner"/> for the same reasons
/// <see cref="VariableHandoffTests"/> does: the real store's persistence is proven elsewhere, and dispatching a
/// real subagent is out of scope. No action-node assertion here (ruling R16): <c>DispatchActionNodeAsync</c>
/// never reads a turn, so no change to this task's code could make such an assertion fail.
/// </remarks>
public sealed class NodeUsageTests : IAsyncLifetime
{
    private static readonly AgentId ImplementerId = AgentId.New();

    /// <summary>A task node that declares outcomes, so it gets an outcome tool and takes <c>BuildNodeResult</c>'s outcome path.</summary>
    private const string WithOutcomesDefYaml = """
        process: with-outcomes
        version: 1
        nodes:
          implement:
            agent: implementer
            skill: implement-change
            outcomes: [done]
            branch: { done: fin }
          fin: { terminal: succeeded }
        """;

    /// <summary>A task node declaring no outcomes at all, so it gets no outcome tool and takes the outcome-less path.</summary>
    private const string NoOutcomesDefYaml = """
        process: no-outcomes
        version: 1
        nodes:
          step: { agent: implementer, skill: implement-change, next: fin }
          fin: { terminal: succeeded }
        """;

    private readonly FakeWorkflowStore _store;
    private readonly InMemoryProcessDefinitionStore _definitions;
    private readonly FakeSubagentRunner _runner;
    private readonly WorkflowNodeDispatcher _dispatcher;

    public NodeUsageTests()
    {
        _definitions = new InMemoryProcessDefinitionStore()
            .Seed(WithOutcomesDefYaml)
            .Seed(NoOutcomesDefYaml);

        var resolver = new FakeWorkflowReferenceResolver(new Dictionary<string, AgentId>(StringComparer.Ordinal)
        {
            ["implementer"] = ImplementerId,
        });

        _store = new FakeWorkflowStore(_definitions);
        _runner = new FakeSubagentRunner();
        _dispatcher = new WorkflowNodeDispatcher(_store, _runner, resolver, _definitions, new InMemorySkillStore(TimeProvider.System), _ => new FakeSecurityContext("workflow-engine"), gates: [], hostActions: []);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Starts <paramref name="process"/> at its declared start node and dispatches the message the start owes it.</summary>
    private async Task<Guid> StartAndDispatchAsync(string process, string startNode, TurnUsage usage, IReadOnlyList<ToolCallSummary> toolCalls)
    {
        var runId = (await _store.StartAsync(new WorkflowStartRequest { Process = process, Version = 1, CorrelationKey = $"c-{process}", StartNode = startNode, InitialVariables = null, StartedBy = TestPrincipals.Starter }, CancellationToken.None)).Value;

        _runner.NextResult = _ => Result<AgentTurnResult, AgentError>.Success(
            new AgentTurnResult(TurnId.New(), SessionId.New(), "reported", usage, toolCalls, TimeSpan.Zero));

        var message = _store.TakeNext(runId) ?? throw new InvalidOperationException("StartAsync owes the start node a dispatch message.");
        await _dispatcher.DispatchAsync(message, CancellationToken.None);
        return runId;
    }

    /// <summary>The tool call an <c>implement</c>-shaped node's agent makes to report the outcome <c>done</c>, with no variables.</summary>
    private static ToolCallSummary DoneOutcomeCall() =>
        new(ToolCallId.New(), WorkflowNodeDispatcher.OutcomeToolName, """{"outcome":"done"}""", true, "ok", TimeSpan.Zero);

    // --- 1. the outcome path carries usage ----------------------------------------------------------------

    /// <summary>
    /// Red if <c>BuildNodeResult</c>'s outcome-path return goes back to constructing its <see cref="NodeResult"/>
    /// with no <see cref="NodeResult.Usage"/> assignment — <see cref="NodeResult.Usage"/> then defaults to
    /// <see langword="null"/> and this equality fails against the non-null usage the turn reported.
    /// </summary>
    [Fact]
    public async Task A_node_that_reports_an_outcome_carries_the_turns_usage_on_its_completed_result()
    {
        var usage = new TurnUsage(1000, 50, "m") { CacheReadTokens = 800, CacheWriteTokens = 100 };

        await StartAndDispatchAsync("with-outcomes", "implement", usage, [DoneOutcomeCall()]);

        _store.CompletedResults.Single().Usage.Should().Be(usage);
    }

    // --- 2. the outcome-less path carries usage too -------------------------------------------------------

    /// <summary>
    /// Red if <c>BuildNodeResult</c>'s outcome-less return (<paramref name="outcomeTool"/> is <see langword="null"/>)
    /// goes back to constructing its <see cref="NodeResult"/> with no <see cref="NodeResult.Usage"/> assignment.
    /// A node with no declared <c>outcomes</c> gets no outcome tool and so makes no tool call at all, which is
    /// exactly why this path needs its own assertion: it is a different <c>return</c> statement in
    /// <c>BuildNodeResult</c> than test 1's, and either one can regress independently of the other.
    /// </summary>
    [Fact]
    public async Task A_node_with_no_declared_outcomes_carries_the_turns_usage_on_its_completed_result()
    {
        var usage = new TurnUsage(1000, 50, "m") { CacheReadTokens = 800, CacheWriteTokens = 100 };

        await StartAndDispatchAsync("no-outcomes", "step", usage, []);

        _store.CompletedResults.Single().Usage.Should().Be(usage);
    }
}
