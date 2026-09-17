using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Thalos.Tests.Subagents.Fakes;
using Thalos.Testing;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos.Tests.Subagents;

public class SubagentRunnerTests
{
    [Fact]
    public async Task Creates_a_session_runs_one_turn_and_closes_it()
    {
        var harness = SubagentRunnerHarness.Create();
        var sessionId = SessionId.New();
        var expected = new AgentTurnResult(
            TurnId.New(), sessionId, "the answer", default, [], TimeSpan.FromSeconds(1));

        harness.Runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(Result<SessionId, AgentError>.Success(sessionId));
        harness.Runtime.RunTurnAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(Result<AgentTurnResult, AgentError>.Success(expected));
        harness.Runtime.CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(UnitResult<AgentError>.Success());

        var result = await harness.Build().RunAsync(SubagentRunnerHarness.Request());

        result.IsSuccess.Should().BeTrue();
        result.Value.Text.Should().Be("the answer");
        await harness.Runtime.Received(1).CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Closes_the_session_even_when_the_turn_fails()
    {
        var harness = SubagentRunnerHarness.Create();
        var sessionId = SessionId.New();

        harness.Runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(Result<SessionId, AgentError>.Success(sessionId));
        harness.Runtime.RunTurnAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(Result<AgentTurnResult, AgentError>.Failure(AgentError.ProviderError("model exploded")));
        harness.Runtime.CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(UnitResult<AgentError>.Success());

        var result = await harness.Build().RunAsync(SubagentRunnerHarness.Request());

        result.ShouldBeFailureWith(AgentErrorCode.ProviderError);
        await harness.Runtime.Received(1).CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Closes_the_session_with_an_uncancellable_token_even_when_the_caller_token_is_already_cancelled()
    {
        // ThalosAgentRuntime.CloseSessionAsync short-circuits on an already-cancelled token before it closes
        // anything (it awaits LoadAuthorizedAsync(sessionId, caller, ct) first). A caller cancelling mid-turn is the
        // normal reason a detached run fails, so the finally must close with CancellationToken.None, never the
        // caller's token, or the session is left Idle exactly when the guarantee matters most.
        var harness = SubagentRunnerHarness.Create();
        var sessionId = SessionId.New();

        harness.Runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(Result<SessionId, AgentError>.Success(sessionId));
        harness.Runtime.RunTurnAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(Result<AgentTurnResult, AgentError>.Failure(AgentError.Cancelled()));
        harness.Runtime.CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(UnitResult<AgentError>.Success());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await harness.Build().RunAsync(SubagentRunnerHarness.Request(), cts.Token);

        result.IsFailure.Should().BeTrue();
        await harness.Runtime.Received(1).CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), CancellationToken.None);
    }

    [Fact]
    public async Task A_failure_to_create_the_session_is_returned_unchanged()
    {
        var harness = SubagentRunnerHarness.Create();
        harness.Runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(Result<SessionId, AgentError>.Failure(AgentError.AgentNotFound(AgentId.New())));

        var result = await harness.Build().RunAsync(SubagentRunnerHarness.Request());

        result.ShouldBeFailureWith(AgentErrorCode.AgentNotFound);
        await harness.Runtime.DidNotReceive().RunTurnAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_session_close_failure_is_logged_with_the_parent_session_id()
    {
        // SubagentRunRequest.ParentSessionId is documented as telemetry lineage but was never actually emitted
        // anywhere. Wiring it into this existing failure log is the minimal fix: the close failure is genuinely
        // unaffected (the run's own result carries the real outcome), but the parent lineage must now appear in the
        // one place this runner already logs a failure.
        var harness = SubagentRunnerHarness.Create();
        var sessionId = SessionId.New();
        var parentSessionId = SessionId.New();
        var logger = new CapturingLogger<SubagentRunner>();
        var expected = new AgentTurnResult(TurnId.New(), sessionId, "the answer", default, [], TimeSpan.FromSeconds(1));

        harness.Runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(Result<SessionId, AgentError>.Success(sessionId));
        harness.Runtime.RunTurnAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(Result<AgentTurnResult, AgentError>.Success(expected));
        harness.Runtime.CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
               .Returns(UnitResult<AgentError>.Failure(AgentError.StoreError("boom")));

        var request = SubagentRunnerHarness.Request() with { ParentSessionId = parentSessionId };
        var result = await harness.Build(logger).RunAsync(request);

        result.IsSuccess.Should().BeTrue("a close failure must not affect the run's own reported outcome");
        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains(sessionId.ToString(), StringComparison.Ordinal)
            && e.Message.Contains(parentSessionId.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void ISubagentRunner_resolves_from_a_configured_container()
    {
        var provider = Substitute.For<IChatClientProvider>();
        provider.Name.Returns("fake");
        provider.DefaultModel.Returns("m");
        provider.CreateChatClient(Arg.Any<AgentDefinition>()).Returns(new ScriptedChatClient().ThenText("ok"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThalos(t =>
        {
            t.UseInMemorySessionStore();
            t.UseChatClientProvider(provider);
        });

        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<ISubagentRunner>().Should().NotBeNull();
    }
}
