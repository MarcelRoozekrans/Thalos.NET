using FluentAssertions; // AwesomeAssertions 7.0.0 namespace
using Microsoft.Extensions.AI;
using Xunit;

namespace Thalos.Testing;

/// <summary>
/// Behavioural contract every <see cref="IAgentSessionStore"/> must satisfy.
/// Derive, implement <see cref="CreateStoreAsync"/>, done. Each test gets a fresh store.
/// </summary>
public abstract class SessionStoreContractTests
{
    protected abstract ValueTask<IAgentSessionStore> CreateStoreAsync();

    private static readonly AgentId Agent = AgentId.New();

    [Fact]
    public async Task Create_then_Get_returns_idle_record_with_zero_counters()
    {
        var store = await CreateStoreAsync();
        var created = await store.CreateAsync(Agent, "owner-1", CancellationToken.None);
        created.IsSuccess.Should().BeTrue();

        var got = await store.GetAsync(created.Value.Id, CancellationToken.None);
        got.IsSuccess.Should().BeTrue();
        got.Value.Should().BeEquivalentTo(created.Value);
        got.Value.State.Should().Be(SessionState.Idle);
        got.Value.TurnCount.Should().Be(0);
        got.Value.OwnerId.Should().Be("owner-1");
        got.Value.AgentId.Should().Be(Agent);
    }

    [Fact]
    public async Task Get_unknown_returns_SessionNotFound()
    {
        var store = await CreateStoreAsync();
        var r = await store.GetAsync(SessionId.New(), CancellationToken.None);
        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be(AgentErrorCode.SessionNotFound);
    }

    [Fact]
    public async Task Messages_append_and_load_in_order_and_preserve_tool_content()
    {
        var store = await CreateStoreAsync();
        var id = (await store.CreateAsync(Agent, "o", CancellationToken.None)).Value.Id;

        var call = new FunctionCallContent("call-1", "roslyn__find_callers", new Dictionary<string, object?>(StringComparer.Ordinal) { ["symbol"] = "Foo.Bar" });
        var batch1 = new List<ChatMessage>
        {
            new(ChatRole.User, "hello"),
            new(ChatRole.Assistant, [call]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "3 callers")]),
        };
        (await store.AppendMessagesAsync(id, batch1, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await store.AppendMessagesAsync(id, [new ChatMessage(ChatRole.Assistant, "Found 3 callers.")], CancellationToken.None)).IsSuccess.Should().BeTrue();

        var loaded = await store.LoadMessagesAsync(id, CancellationToken.None);
        loaded.IsSuccess.Should().BeTrue();
        loaded.Value.Should().HaveCount(4);
        loaded.Value[0].Text.Should().Be("hello");
        loaded.Value[1].Contents.OfType<FunctionCallContent>().Single().Name.Should().Be("roslyn__find_callers");
        loaded.Value[2].Contents.OfType<FunctionResultContent>().Single().CallId.Should().Be("call-1");
        loaded.Value[3].Text.Should().Be("Found 3 callers.");
    }

    [Fact]
    public async Task RecordTurn_increments_counters_and_bumps_activity()
    {
        var store = await CreateStoreAsync();
        var created = (await store.CreateAsync(Agent, "o", CancellationToken.None)).Value;

        (await store.RecordTurnAsync(created.Id, new TurnUsage(100, 20, "m"), CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await store.RecordTurnAsync(created.Id, new TurnUsage(50, 10, "m"), CancellationToken.None)).IsSuccess.Should().BeTrue();

        var got = (await store.GetAsync(created.Id, CancellationToken.None)).Value;
        got.TurnCount.Should().Be(2);
        got.TotalInputTokens.Should().Be(150);
        got.TotalOutputTokens.Should().Be(30);
        got.LastActivityAt.Should().BeOnOrAfter(created.LastActivityAt);
    }

    [Fact]
    public async Task UpdateState_persists()
    {
        var store = await CreateStoreAsync();
        var id = (await store.CreateAsync(Agent, "o", CancellationToken.None)).Value.Id;

        (await store.UpdateStateAsync(id, SessionState.Running, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await store.GetAsync(id, CancellationToken.None)).Value.State.Should().Be(SessionState.Running);
    }

    [Fact]
    public async Task List_filters_by_owner_and_pages_newest_first()
    {
        var store = await CreateStoreAsync();
        var a1 = (await store.CreateAsync(Agent, "alice", CancellationToken.None)).Value.Id;
        await Task.Delay(5);
        var a2 = (await store.CreateAsync(Agent, "alice", CancellationToken.None)).Value.Id;
        await store.CreateAsync(Agent, "bob", CancellationToken.None);

        var page = (await store.ListAsync("alice", skip: 0, take: 10, CancellationToken.None)).Value;
        page.Select(s => s.Id).Should().Equal(a2, a1);

        (await store.ListAsync("alice", skip: 1, take: 1, CancellationToken.None)).Value.Select(s => s.Id).Should().Equal(a1);
        (await store.ListAsync("nobody", 0, 10, CancellationToken.None)).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Operations_on_unknown_session_fail_with_SessionNotFound()
    {
        var store = await CreateStoreAsync();
        var id = SessionId.New();
        (await store.LoadMessagesAsync(id, CancellationToken.None)).Error.Code.Should().Be(AgentErrorCode.SessionNotFound);
        (await store.AppendMessagesAsync(id, [new ChatMessage(ChatRole.User, "x")], CancellationToken.None)).Error.Code.Should().Be(AgentErrorCode.SessionNotFound);
        (await store.RecordTurnAsync(id, TurnUsage.Empty("m"), CancellationToken.None)).Error.Code.Should().Be(AgentErrorCode.SessionNotFound);
        (await store.UpdateStateAsync(id, SessionState.Closed, CancellationToken.None)).Error.Code.Should().Be(AgentErrorCode.SessionNotFound);
    }
}
