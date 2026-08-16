using Microsoft.Extensions.AI;

namespace Thalos.Tests.Unit.Runtime;

/// <summary>Session integrity on exceptional paths: whatever fails after the claim, the session ends Idle with exactly one terminal error.</summary>
public sealed class ThalosAgentRuntimeIntegrityTests
{
    [Fact]
    public async Task Publisher_throwing_on_TurnStarted_fails_the_turn_once_and_releases_the_session()
    {
        var f = new RuntimeFixture { PublisherDecorator = inner => new ThrowingPublisher<TurnStartedNotification>(inner) }.Build();
        f.Client.ThenText("never");
        var s = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User(), default)).Value;

        var events = await f.Runtime.RunTurnStreamingAsync(new AgentTurnRequest(s, "hi", RuntimeFixture.User()), default).ToListAsync();

        events.Should().ContainSingle().Which.Should().BeOfType<TurnFailedEvent>();
        (await f.Store.GetAsync(s, default)).Value.State.Should().Be(SessionState.Idle);
        f.Publisher.Of<TurnFailedNotification>().Should().ContainSingle();
        f.Client.Requests.Should().BeEmpty(); // the model was never called
    }

    [Fact]
    public async Task Publisher_throwing_on_TurnCompleted_does_not_fail_the_persisted_turn()
    {
        var f = new RuntimeFixture { PublisherDecorator = inner => new ThrowingPublisher<TurnCompletedNotification>(inner) }.Build();
        f.Client.ThenText("hello");
        var s = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User(), default)).Value;

        var r = await f.Runtime.RunTurnAsync(new AgentTurnRequest(s, "hi", RuntimeFixture.User()), default);

        r.IsSuccess.Should().BeTrue();
        r.Value.Text.Should().Be("hello");
        var record = (await f.Store.GetAsync(s, default)).Value;
        record.State.Should().Be(SessionState.Idle);
        record.TurnCount.Should().Be(1);
        f.Publisher.Of<TurnFailedNotification>().Should().BeEmpty();
    }

    [Fact]
    public async Task Publisher_throwing_on_SessionCreated_does_not_fail_session_creation()
    {
        var f = new RuntimeFixture { PublisherDecorator = inner => new ThrowingPublisher<SessionCreatedNotification>(inner) }.Build();

        var created = await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User(), default);

        created.IsSuccess.Should().BeTrue();
        (await f.Store.GetAsync(created.Value, default)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Store_throwing_in_RecordTurn_fails_with_StoreError_and_releases_the_session()
    {
        var f = new RuntimeFixture { StoreDecorator = inner => new FaultingSessionStore(inner) { OnRecordTurn = () => throw new InvalidOperationException("db down") } }.Build();
        f.Client.ThenText("hello");
        var s = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User(), default)).Value;

        var r = await f.Runtime.RunTurnAsync(new AgentTurnRequest(s, "hi", RuntimeFixture.User()), default);

        r.Error.Code.Should().Be(AgentErrorCode.StoreError);
        r.Error.Detail.Should().Be("db down");
        (await f.Store.GetAsync(s, default)).Value.State.Should().Be(SessionState.Idle);
        f.Publisher.Of<TurnCompletedNotification>().Should().BeEmpty();
    }

    [Fact]
    public async Task Store_failure_in_RecordTurn_after_deltas_streams_the_deltas_then_a_single_StoreError()
    {
        var f = new RuntimeFixture { StoreDecorator = inner => new FaultingSessionStore(inner) { OnRecordTurn = () => ZeroAlloc.Results.UnitResult<AgentError>.Failure(AgentError.StoreError("disk full")) } }.Build();
        f.Client.ThenText("a b c");
        var s = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User(), default)).Value;

        var events = await f.Runtime.RunTurnStreamingAsync(new AgentTurnRequest(s, "hi", RuntimeFixture.User()), default).ToListAsync();

        events.Select(e => e.Kind).Should().Equal("text-delta", "text-delta", "text-delta", "error");
        events.OfType<TurnFailedEvent>().Single().Error.Should().Be(AgentError.StoreError("disk full"));
        (await f.Store.GetAsync(s, default)).Value.State.Should().Be(SessionState.Idle);
        f.Publisher.Of<TurnCompletedNotification>().Should().BeEmpty();
        f.Publisher.Of<TurnFailedNotification>().Should().ContainSingle(n => n.Error.Code == AgentErrorCode.StoreError);
    }

    [Fact]
    public async Task Agent_factory_failure_yields_a_single_ProviderError_and_releases_the_session()
    {
        var f = new RuntimeFixture { ChatClientFactory = _ => throw new InvalidOperationException("no api key") }.Build();
        var s = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User(), default)).Value;

        var events = await f.Runtime.RunTurnStreamingAsync(new AgentTurnRequest(s, "hi", RuntimeFixture.User()), default).ToListAsync();

        var failed = events.Should().ContainSingle().Which.Should().BeOfType<TurnFailedEvent>().Which;
        failed.Error.Code.Should().Be(AgentErrorCode.ProviderError);
        failed.Error.Detail.Should().Be("no api key");
        (await f.Store.GetAsync(s, default)).Value.State.Should().Be(SessionState.Idle);
    }

    [Fact]
    public async Task Concurrent_turns_on_two_sessions_of_the_same_agent_complete_independently()
    {
        var f = new RuntimeFixture { ChatClientFactory = _ => new EchoChatClient() }.Build();
        var a = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User("alice"), default)).Value;
        var b = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User("bob"), default)).Value;

        var turns = await Task.WhenAll(
            f.Runtime.RunTurnAsync(new AgentTurnRequest(a, "one", RuntimeFixture.User("alice")), default).AsTask(),
            f.Runtime.RunTurnAsync(new AgentTurnRequest(b, "two", RuntimeFixture.User("bob")), default).AsTask());

        turns[0].Value.Text.Should().Be("echo:one");
        turns[1].Value.Text.Should().Be("echo:two");
        turns.Should().AllSatisfy(t => t.Value.Usage.Should().Be(new TurnUsage(7, 2, "m1")));
        turns[0].Value.SessionId.Should().Be(a);
        turns[1].Value.SessionId.Should().Be(b);
        (await f.Store.LoadMessagesAsync(a, default)).Value.Select(m => m.Text).Should().Equal("one", "echo:one");
        (await f.Store.LoadMessagesAsync(b, default)).Value.Select(m => m.Text).Should().Equal("two", "echo:two");
        (await f.Store.GetAsync(a, default)).Value.State.Should().Be(SessionState.Idle);
        (await f.Store.GetAsync(b, default)).Value.State.Should().Be(SessionState.Idle);
    }

    [Fact]
    public async Task Pre_claim_failure_is_returned_to_the_caller_but_not_published_to_the_hub()
    {
        var f = new RuntimeFixture().Build();
        var seen = new List<AgentEvent>();
        using var _ = f.Hub.Subscribe((e, ct) => { lock (seen) seen.Add(e); return default; });
        var s = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User("alice"), default)).Value;

        var events = await f.Runtime.RunTurnStreamingAsync(new AgentTurnRequest(s, "hi", RuntimeFixture.User("mallory")), default).ToListAsync();

        events.Should().ContainSingle().Which.Should().BeOfType<TurnFailedEvent>().Which.Error.Code.Should().Be(AgentErrorCode.Unauthorized);
        seen.Should().BeEmpty();
        f.Publisher.Of<TurnFailedNotification>().Should().ContainSingle(n => n.Error.Code == AgentErrorCode.Unauthorized);
        (await f.Store.GetAsync(s, default)).Value.State.Should().Be(SessionState.Idle);
    }

    [Fact]
    public async Task Usage_adopts_the_provider_reported_model_when_the_definition_pins_none()
    {
        var f = new RuntimeFixture().WithModel(null).Build();
        f.Client.ThenText("hi there", input: 4, output: 2);
        var s = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User(), default)).Value;

        var r = await f.Runtime.RunTurnAsync(new AgentTurnRequest(s, "hi", RuntimeFixture.User()), default);

        r.Value.Usage.Should().Be(new TurnUsage(4, 2, "scripted-model"));
    }

    [Fact]
    public async Task Usage_keeps_the_pinned_model_over_the_provider_reported_one()
    {
        var f = new RuntimeFixture().Build(); // Model = "m1"
        f.Client.ThenText("hi", input: 1, output: 1);
        var s = (await f.Runtime.CreateSessionAsync(f.Agent.Id, RuntimeFixture.User(), default)).Value;

        var r = await f.Runtime.RunTurnAsync(new AgentTurnRequest(s, "hi", RuntimeFixture.User()), default);

        r.Value.Usage.ModelId.Should().Be("m1");
    }
}
