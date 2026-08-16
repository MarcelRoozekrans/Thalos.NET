using FluentAssertions; // AwesomeAssertions 7.0.0 namespace
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Thalos.Testing;

/// <summary>
/// Behavioural contract every <see cref="IAgentSessionStore"/> implementation must satisfy — the same suite Thalos runs
/// against its in-memory store. To use it, derive a public test class in your test project, implement
/// <see cref="CreateStoreAsync"/> to return a fresh, empty store that reads time from the given <see cref="TimeProvider"/>
/// (a <see cref="FakeTimeProvider"/> the suite advances between operations), and let xUnit discover the inherited
/// <c>[Fact]</c>s. Every test gets its own store, so per-test isolation (e.g. a schema or table prefix per call) is
/// the implementer's job.
/// </summary>
/// <remarks>
/// Timestamps are compared with a 1 ms tolerance: stores must persist <c>CreatedAt</c>/<c>LastActivityAt</c> with at
/// least millisecond precision. Messages must round-trip <see cref="FunctionCallContent"/>/<see cref="FunctionResultContent"/>
/// (serialize with <c>AIJsonUtilities.DefaultOptions</c>). Concurrent appends and turn records on the same session must
/// not lose writes.
/// </remarks>
public abstract class SessionStoreContractTests
{
    /// <summary>Creates a fresh, empty store whose clock is <paramref name="clock"/>.</summary>
    protected abstract ValueTask<IAgentSessionStore> CreateStoreAsync(TimeProvider clock);

    private static readonly AgentId Agent = AgentId.New();
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(1);

    private static FakeTimeProvider NewClock() => new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Create_then_Get_returns_idle_record_with_zero_counters()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var created = await store.CreateAsync(Agent, "owner-1", CancellationToken.None);
        created.IsSuccess.Should().BeTrue();

        var got = await store.GetAsync(created.Value.Id, CancellationToken.None);
        got.IsSuccess.Should().BeTrue();
        got.Value.Should().BeEquivalentTo(created.Value, o => o.Excluding(r => r.CreatedAt).Excluding(r => r.LastActivityAt));
        got.Value.CreatedAt.Should().BeCloseTo(created.Value.CreatedAt, Tolerance);
        got.Value.LastActivityAt.Should().BeCloseTo(created.Value.LastActivityAt, Tolerance);
        got.Value.State.Should().Be(SessionState.Idle);
        got.Value.TurnCount.Should().Be(0);
        got.Value.TotalInputTokens.Should().Be(0);
        got.Value.TotalOutputTokens.Should().Be(0);
        got.Value.OwnerId.Should().Be("owner-1");
        got.Value.AgentId.Should().Be(Agent);
    }

    [Fact]
    public async Task Create_sets_CreatedAt_equal_to_LastActivityAt()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var created = (await store.CreateAsync(Agent, "o", CancellationToken.None)).Value;

        created.CreatedAt.Should().Be(created.LastActivityAt);
        created.CreatedAt.Should().BeCloseTo(clock.GetUtcNow(), Tolerance);

        var got = (await store.GetAsync(created.Id, CancellationToken.None)).Value;
        got.CreatedAt.Should().BeCloseTo(got.LastActivityAt, Tolerance);
    }

    [Fact]
    public async Task Get_unknown_returns_SessionNotFound()
    {
        var store = await CreateStoreAsync(NewClock());
        var r = await store.GetAsync(SessionId.New(), CancellationToken.None);
        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be(AgentErrorCode.SessionNotFound);
    }

    [Fact]
    public async Task LoadMessages_on_fresh_session_is_empty()
    {
        var store = await CreateStoreAsync(NewClock());
        var id = (await store.CreateAsync(Agent, "o", CancellationToken.None)).Value.Id;

        var loaded = await store.LoadMessagesAsync(id, CancellationToken.None);
        loaded.IsSuccess.Should().BeTrue();
        loaded.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Messages_append_and_load_in_order_and_preserve_tool_content()
    {
        var store = await CreateStoreAsync(NewClock());
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
    public async Task Append_empty_list_is_a_successful_no_op()
    {
        var store = await CreateStoreAsync(NewClock());
        var id = (await store.CreateAsync(Agent, "o", CancellationToken.None)).Value.Id;

        (await store.AppendMessagesAsync(id, [], CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await store.LoadMessagesAsync(id, CancellationToken.None)).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task RecordTurn_increments_counters_and_bumps_activity()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var created = (await store.CreateAsync(Agent, "o", CancellationToken.None)).Value;

        clock.Advance(TimeSpan.FromSeconds(1));
        (await store.RecordTurnAsync(created.Id, new TurnUsage(100, 20, "m"), CancellationToken.None)).IsSuccess.Should().BeTrue();
        clock.Advance(TimeSpan.FromSeconds(1));
        (await store.RecordTurnAsync(created.Id, new TurnUsage(50, 10, "m"), CancellationToken.None)).IsSuccess.Should().BeTrue();

        var got = (await store.GetAsync(created.Id, CancellationToken.None)).Value;
        got.TurnCount.Should().Be(2);
        got.TotalInputTokens.Should().Be(150);
        got.TotalOutputTokens.Should().Be(30);
        got.LastActivityAt.Should().BeCloseTo(clock.GetUtcNow(), Tolerance);
        got.LastActivityAt.Should().BeAfter(created.LastActivityAt);
        got.CreatedAt.Should().BeCloseTo(created.CreatedAt, Tolerance);
    }

    [Fact]
    public async Task UpdateState_persists()
    {
        var store = await CreateStoreAsync(NewClock());
        var id = (await store.CreateAsync(Agent, "o", CancellationToken.None)).Value.Id;

        (await store.UpdateStateAsync(id, SessionState.Running, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await store.GetAsync(id, CancellationToken.None)).Value.State.Should().Be(SessionState.Running);
    }

    [Fact]
    public async Task UpdateState_bumps_LastActivityAt()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var created = (await store.CreateAsync(Agent, "o", CancellationToken.None)).Value;

        clock.Advance(TimeSpan.FromSeconds(1));
        (await store.UpdateStateAsync(created.Id, SessionState.Closed, CancellationToken.None)).IsSuccess.Should().BeTrue();

        var got = (await store.GetAsync(created.Id, CancellationToken.None)).Value;
        got.LastActivityAt.Should().BeCloseTo(clock.GetUtcNow(), Tolerance);
        got.LastActivityAt.Should().BeAfter(created.LastActivityAt);
        got.CreatedAt.Should().BeCloseTo(created.CreatedAt, Tolerance);
    }

    [Fact]
    public async Task List_filters_by_owner_and_pages_newest_first()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var a1 = (await store.CreateAsync(Agent, "alice", CancellationToken.None)).Value.Id;
        clock.Advance(TimeSpan.FromSeconds(1));
        var a2 = (await store.CreateAsync(Agent, "alice", CancellationToken.None)).Value.Id;
        clock.Advance(TimeSpan.FromSeconds(1));
        await store.CreateAsync(Agent, "bob", CancellationToken.None);

        var page = (await store.ListAsync("alice", skip: 0, take: 10, CancellationToken.None)).Value;
        page.Select(s => s.Id).Should().Equal(a2, a1);

        (await store.ListAsync("alice", skip: 1, take: 1, CancellationToken.None)).Value.Select(s => s.Id).Should().Equal(a1);
        (await store.ListAsync("nobody", 0, 10, CancellationToken.None)).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task List_skip_beyond_count_is_empty()
    {
        var store = await CreateStoreAsync(NewClock());
        await store.CreateAsync(Agent, "alice", CancellationToken.None);
        await store.CreateAsync(Agent, "alice", CancellationToken.None);

        var page = await store.ListAsync("alice", skip: 5, take: 10, CancellationToken.None);
        page.IsSuccess.Should().BeTrue();
        page.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Concurrent_appends_and_turn_records_on_one_session_lose_nothing()
    {
        var store = await CreateStoreAsync(NewClock());
        var id = (await store.CreateAsync(Agent, "o", CancellationToken.None)).Value.Id;

        const int n = 20;
        var work = new List<Task>(2 * n);
        for (var i = 0; i < n; i++)
        {
            var seq = i;
            work.Add(Task.Run(async () =>
                (await store.AppendMessagesAsync(id, [new ChatMessage(ChatRole.User, $"m{seq}")], CancellationToken.None).ConfigureAwait(false)).IsSuccess.Should().BeTrue()));
            work.Add(Task.Run(async () =>
                (await store.RecordTurnAsync(id, new TurnUsage(1, 1, "m"), CancellationToken.None).ConfigureAwait(false)).IsSuccess.Should().BeTrue()));
        }

        await Task.WhenAll(work);

        var loaded = (await store.LoadMessagesAsync(id, CancellationToken.None)).Value;
        loaded.Should().HaveCount(n);
        loaded.Select(m => m.Text).Should().OnlyHaveUniqueItems();

        var got = (await store.GetAsync(id, CancellationToken.None)).Value;
        got.TurnCount.Should().Be(n);
        got.TotalInputTokens.Should().Be(n);
        got.TotalOutputTokens.Should().Be(n);
    }

    [Fact]
    public async Task Operations_on_unknown_session_fail_with_SessionNotFound()
    {
        var store = await CreateStoreAsync(NewClock());
        var id = SessionId.New();
        (await store.LoadMessagesAsync(id, CancellationToken.None)).Error.Code.Should().Be(AgentErrorCode.SessionNotFound);
        (await store.AppendMessagesAsync(id, [new ChatMessage(ChatRole.User, "x")], CancellationToken.None)).Error.Code.Should().Be(AgentErrorCode.SessionNotFound);
        (await store.RecordTurnAsync(id, TurnUsage.Empty("m"), CancellationToken.None)).Error.Code.Should().Be(AgentErrorCode.SessionNotFound);
        (await store.UpdateStateAsync(id, SessionState.Closed, CancellationToken.None)).Error.Code.Should().Be(AgentErrorCode.SessionNotFound);
    }
}
