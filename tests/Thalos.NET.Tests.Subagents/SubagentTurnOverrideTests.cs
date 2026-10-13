using NSubstitute;
using ZeroAlloc.Results;

namespace Thalos.Tests.Subagents;

/// <summary>
/// <see cref="SubagentRunRequest.Model"/> and <see cref="SubagentRunRequest.ToolFilter"/> are inert unless the runner
/// forwards them onto the turn it starts: the runtime validates and applies them from the <see cref="AgentTurnRequest"/> alone.
/// </summary>
public sealed class SubagentTurnOverrideTests
{
    private static SubagentRunnerHarness ReadyHarness()
    {
        var harness = SubagentRunnerHarness.Create();
        harness.Runtime.CreateSessionAsync(default, default!, default)
            .ReturnsForAnyArgs(Result<SessionId, AgentError>.Success(SessionId.New()));
        harness.Runtime.RunTurnAsync(default!, default)
            .ReturnsForAnyArgs(Result<AgentTurnResult, AgentError>.Success(
                new AgentTurnResult(TurnId.New(), SessionId.New(), "text", TurnUsage.Empty("m"), [], TimeSpan.Zero)));
        harness.Runtime.CloseSessionAsync(default, default!, default)
            .ReturnsForAnyArgs(UnitResult<AgentError>.Success());
        return harness;
    }

    /// <summary>
    /// Red: dropping <c>Model = request.Model</c> or <c>ToolFilter = request.ToolFilter</c> from
    /// <c>SubagentRunner.RunTurnAsync</c>; each is its own conjunct of the matcher, so each goes red alone.
    /// </summary>
    [Fact]
    public async Task The_model_and_tool_filter_are_forwarded_onto_the_turn()
    {
        var harness = ReadyHarness();
        string[] filter = ["workspace__*", "daedalus__report"];
        var request = SubagentRunnerHarness.Request() with { Model = "big-model", ToolFilter = filter };

        var result = await harness.Build().RunAsync(request, default);

        result.IsSuccess.Should().BeTrue();
        await harness.Runtime.Received(1).RunTurnAsync(
            Arg.Is<AgentTurnRequest>(r => r.Model == "big-model"
                && r.ToolFilter != null && r.ToolFilter.SequenceEqual(filter)),
            Arg.Any<CancellationToken>());
    }

    /// <summary>Red: the runner invents a model or an empty filter; an empty filter would mean "no tools", not "no filter".</summary>
    [Fact]
    public async Task A_run_without_overrides_leaves_both_null_on_the_turn()
    {
        var harness = ReadyHarness();

        var result = await harness.Build().RunAsync(SubagentRunnerHarness.Request(), default);

        result.IsSuccess.Should().BeTrue();
        await harness.Runtime.Received(1).RunTurnAsync(
            Arg.Is<AgentTurnRequest>(r => r.Model == null && r.ToolFilter == null), Arg.Any<CancellationToken>());
    }
}
