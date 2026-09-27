using AwesomeAssertions;
using NSubstitute;
using Thalos.Testing;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos.Tests.Subagents;

public class SubagentBudgetTests
{
    private const string OutcomeTool = "report_outcome";

    [Fact]
    public async Task The_budget_travels_on_the_turn_request_to_the_runtime()
    {
        var harness = SubagentRunnerHarness.Create();
        var sessionId = SessionId.New();
        ArrangeSession(harness, sessionId, new AgentTurnResult(TurnId.New(), sessionId, "ok", UsageOf(100, 0), [], TimeSpan.FromSeconds(1)));

        var result = await harness.Build().RunAsync(SubagentRunnerHarness.Request(budget: new SubagentBudget(150, TimeSpan.FromMinutes(10))));

        result.IsSuccess.Should().BeTrue();
        await harness.Runtime.Received(1).RunTurnAsync(
            Arg.Is<AgentTurnRequest>(r => r.MaxTotalTokens == 150), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_host_configured_default_budget_applies_when_the_request_omits_one()
    {
        // SubagentRunRequest.Budget is null here (Request() below is not given one), so the runner must fall back to
        // SubagentOptions.DefaultBudget - not the static SubagentBudget.Default, which carries 50,000. A runner that
        // resolved the static default instead of the host's configured one would forward MaxTotalTokens == 50000,
        // not 1000.
        var harness = SubagentRunnerHarness.Create();
        harness.Options.DefaultBudget = new SubagentBudget(1_000, TimeSpan.FromMinutes(10));
        var sessionId = SessionId.New();
        ArrangeSession(harness, sessionId, new AgentTurnResult(TurnId.New(), sessionId, "ok", UsageOf(100, 0), [], TimeSpan.FromSeconds(1)));

        var result = await harness.Build().RunAsync(SubagentRunnerHarness.Request(budget: null));

        result.IsSuccess.Should().BeTrue();
        await harness.Runtime.Received(1).RunTurnAsync(
            Arg.Is<AgentTurnRequest>(r => r.MaxTotalTokens == 1_000), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_turn_that_records_blocked_within_budget_keeps_blocked_even_when_its_total_went_over()
    {
        // The phase 2.4 regression: 737,634 tokens spent, `blocked` recorded, then discarded post hoc. The runtime let
        // the last round trip start under the ceiling; the turn ended normally, so its outcome stands.
        var harness = SubagentRunnerHarness.Create();
        var sessionId = SessionId.New();
        var blocked = new ToolCallSummary(
            ToolCallId.New(), OutcomeTool, """{"outcome":"blocked"}""", Succeeded: true, "ok", TimeSpan.FromMilliseconds(1));
        ArrangeSession(harness, sessionId, new AgentTurnResult(TurnId.New(), sessionId, "done", UsageOf(700, 20), [blocked], TimeSpan.FromSeconds(1)));

        var result = await harness.Build().RunAsync(SubagentRunnerHarness.Request(budget: new SubagentBudget(600, TimeSpan.FromMinutes(5))));

        result.IsSuccess.Should().BeTrue("720 tokens is over 600, but the runtime, not the runner, decides whether a round trip may start");
        result.Value.ToolCalls.Should().ContainSingle(c => c.ToolName == OutcomeTool && c.ArgumentsJson.Contains("blocked"));
        result.Value.Usage.InputTokens.Should().Be(700);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_non_positive_max_total_tokens_is_rejected_without_creating_a_session(int maxTotalTokens)
    {
        // Zero or negative must be refused by ValidateRequest before any session is created - otherwise the session
        // is created and the turn is run (spending exactly the money this guard exists to prevent) only to fail
        // afterwards, once the runtime's own ceiling compares usage against a cap that can never be satisfied.
        var harness = SubagentRunnerHarness.Create();
        var request = SubagentRunnerHarness.Request(budget: new SubagentBudget(maxTotalTokens, TimeSpan.FromMinutes(10)));

        var result = await harness.Build().RunAsync(request);

        result.ShouldBeFailureWith(AgentErrorCode.Validation);
        result.Error.Message.Should().Contain("token budget must be positive");
        await harness.Runtime.DidNotReceive().CreateSessionAsync(
            Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_turn_inside_the_budget_succeeds()
    {
        var harness = SubagentRunnerHarness.Create();
        var sessionId = SessionId.New();
        ArrangeSession(harness, sessionId, new AgentTurnResult(TurnId.New(), sessionId, "short answer", UsageOf(100, 0), [], TimeSpan.FromSeconds(1)));

        var request = SubagentRunnerHarness.Request(budget: new SubagentBudget(5_000, TimeSpan.FromMinutes(10)));
        var result = await harness.Build().RunAsync(request);

        result.IsSuccess.Should().BeTrue();
    }

    /// <summary>Stubs CreateSessionAsync, RunTurnAsync and CloseSessionAsync on <paramref name="harness"/>.Runtime for the
    /// common case: a session is created, one turn returns <paramref name="turnResult"/>, and the session closes cleanly.</summary>
    private static void ArrangeSession(SubagentRunnerHarness harness, SessionId sessionId, AgentTurnResult turnResult)
    {
        harness.Runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(Result<SessionId, AgentError>.Success(sessionId));
        harness.Runtime.RunTurnAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(Result<AgentTurnResult, AgentError>.Success(turnResult));
        harness.Runtime.CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(UnitResult<AgentError>.Success());
    }

    // TurnUsage is (int InputTokens, int OutputTokens, string ModelId) — verified against
    // src/Thalos.NET.Abstractions/Turns/TurnUsage.cs. The third parameter is required.
    private static TurnUsage UsageOf(int inputTokens, int outputTokens) => new(inputTokens, outputTokens, "test-model");
}
