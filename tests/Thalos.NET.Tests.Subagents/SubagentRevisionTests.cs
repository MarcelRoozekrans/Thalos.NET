using NSubstitute;
using ZeroAlloc.Results;

namespace Thalos.Tests.Subagents;

/// <summary>
/// <see cref="SubagentRunRequest.AgentRevision"/> is inert unless the runner forwards it onto the turn it starts: the
/// runtime resolves the pinned <see cref="AgentDefinition"/> from <see cref="AgentTurnRequest.AgentRevision"/> alone.
/// These tests assert the value reaches that request.
/// </summary>
public sealed class SubagentRevisionTests
{
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
    /// Red the moment the <c>AgentRevision = request.AgentRevision</c> initializer is dropped from
    /// <c>SubagentRunner.RunTurnAsync</c> — load-bearing for Task A7, whose dispatcher sets
    /// <see cref="SubagentRunRequest.AgentRevision"/> and relies on it reaching the turn.
    /// </summary>
    [Fact]
    public async Task Agent_revision_is_forwarded_onto_the_turn_request()
    {
        var harness = ReadyHarness();
        var request = SubagentRunnerHarness.Request() with { AgentRevision = "r7" };

        var result = await harness.Build().RunAsync(request, default);

        result.IsSuccess.Should().BeTrue();
        await harness.Runtime.Received(1).RunTurnAsync(
            Arg.Is<AgentTurnRequest>(r => r.AgentRevision == "r7"), Arg.Any<CancellationToken>());
    }

    /// <summary>The other half: a run that pins no revision must not invent one.</summary>
    [Fact]
    public async Task Run_without_an_agent_revision_leaves_the_turn_request_unpinned()
    {
        var harness = ReadyHarness();

        var result = await harness.Build().RunAsync(SubagentRunnerHarness.Request(), default);

        result.IsSuccess.Should().BeTrue();
        await harness.Runtime.Received(1).RunTurnAsync(
            Arg.Is<AgentTurnRequest>(r => r.AgentRevision == null), Arg.Any<CancellationToken>());
    }
}
