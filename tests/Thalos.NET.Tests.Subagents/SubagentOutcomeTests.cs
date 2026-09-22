using NSubstitute;
using ZeroAlloc.Results;

namespace Thalos.Tests.Subagents;

/// <summary>
/// <see cref="SubagentRunRequest.RequiredOutcome"/> is inert unless the runner forwards it onto the turn it starts:
/// the runtime is the only component that can offer the tool, and <see cref="AgentTurnRequest"/> is the only thing
/// it is given. These tests assert the value reaches that request.
/// </summary>
public sealed class SubagentOutcomeTests
{
    private static readonly OutcomeToolSchema Approval = new("workflow__report_outcome", ["approved", "rejected"]);

    private static AgentTurnResult EmptyTurn() =>
        new(TurnId.New(), SessionId.New(), "text", TurnUsage.Empty("m"), [], TimeSpan.Zero);

    private static SubagentRunnerHarness ReadyHarness()
    {
        var harness = SubagentRunnerHarness.Create();
        harness.Runtime.CreateSessionAsync(default, default!, default)
            .ReturnsForAnyArgs(Result<SessionId, AgentError>.Success(SessionId.New()));
        harness.Runtime.RunTurnAsync(default!, default)
            .ReturnsForAnyArgs(Result<AgentTurnResult, AgentError>.Success(EmptyTurn()));
        harness.Runtime.CloseSessionAsync(default, default!, default)
            .ReturnsForAnyArgs(UnitResult<AgentError>.Success());
        return harness;
    }

    /// <summary>
    /// Red the moment the <c>RequiredOutcome = request.RequiredOutcome</c> initializer is dropped from
    /// <c>SubagentRunner.RunTurnAsync</c> — which is exactly the state this task found the code in.
    /// </summary>
    [Fact]
    public async Task Required_outcome_is_forwarded_onto_the_turn_request()
    {
        var harness = ReadyHarness();
        var request = SubagentRunnerHarness.Request() with { RequiredOutcome = Approval };

        var result = await harness.Build().RunAsync(request, default);

        result.IsSuccess.Should().BeTrue();
        await harness.Runtime.Received(1).RunTurnAsync(
            Arg.Is<AgentTurnRequest>(r => ReferenceEquals(r.RequiredOutcome, Approval)), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The other half: a run that declares no outcome must not invent one. Red if the runner ever substituted a
    /// default schema for a missing one.
    /// </summary>
    [Fact]
    public async Task Run_without_a_required_outcome_leaves_the_turn_request_unconstrained()
    {
        var harness = ReadyHarness();

        var result = await harness.Build().RunAsync(SubagentRunnerHarness.Request(), default);

        result.IsSuccess.Should().BeTrue();
        await harness.Runtime.Received(1).RunTurnAsync(
            Arg.Is<AgentTurnRequest>(r => r.RequiredOutcome == null), Arg.Any<CancellationToken>());
    }
}
