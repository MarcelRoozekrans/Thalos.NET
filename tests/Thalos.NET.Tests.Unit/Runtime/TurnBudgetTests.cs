using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Thalos.Runtime;
using Thalos.Testing;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos.Tests.Unit.Runtime;

/// <summary>
/// <see cref="AgentTurnRequest.MaxTotalTokens"/> end to end over the real runtime, agent factory and MAF
/// function-invocation loop, which drives the provider through its streaming path, plus
/// <see cref="TurnBudgetChatClient"/> directly for the non-streaming path, missing usage and concurrent round trips.
/// </summary>
public sealed class TurnBudgetTests
{
    private static readonly ISecurityContext Caller = RuntimeFixture.User();

    private static AIFunction Echo() => AIFunctionFactory.Create((string text) => "echo:" + text, "echo");

    /// <summary>Four tool calls then text, each round trip reporting 50 input and 50 output tokens.</summary>
    private static async Task<(RuntimeFixture Fixture, SessionId Session)> FiveCallLoopFixtureAsync()
    {
        var f = new RuntimeFixture().WithTool(Echo()).Build();
        for (var i = 0; i < 4; i++)
        {
            f.Client.ThenToolCall("t__echo", new { text = "x" }, input: 50, output: 50);
        }

        f.Client.ThenText("done", input: 50, output: 50);
        var session = await f.Runtime.CreateSessionAsync(f.Agent.Id, Caller, CancellationToken.None);
        return (f, session.Value);
    }

    private static async Task<(ThalosAgentRuntime Runtime, ScriptedChatClient Scripted, SessionId Session)> FiveCallLoopAsync()
    {
        var (f, session) = await FiveCallLoopFixtureAsync();
        return (f.Runtime, f.Client, session);
    }

    // ---------- through the runtime (streaming path) ----------

    [Fact]
    public async Task A_five_call_loop_with_a_budget_for_two_stops_before_call_three()
    {
        var (runtime, scripted, session) = await FiveCallLoopAsync();
        var result = await runtime.RunTurnAsync(new AgentTurnRequest(session, "go", Caller) { MaxTotalTokens = 200 }, CancellationToken.None);

        scripted.Requests.Should().HaveCount(2);
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.SubagentBudgetExceeded);
        result.Error.Message.Should().Contain("round trip 3").And.Contain("200 tokens used").And.Contain("budget of 200");
        result.Error.Message.Should().Be("Stopped before model round trip 3: 200 tokens used of a budget of 200.");
    }

    [Fact]
    public async Task Without_a_budget_all_five_calls_run()
    {
        var (runtime, scripted, session) = await FiveCallLoopAsync();
        var result = await runtime.RunTurnAsync(new AgentTurnRequest(session, "go", Caller), CancellationToken.None);

        scripted.Requests.Should().HaveCount(5);
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task The_failed_turn_still_reports_the_tokens_it_spent()
    {
        var (f, session) = await FiveCallLoopFixtureAsync();

        var events = await f.Runtime.RunTurnStreamingAsync(new AgentTurnRequest(session, "go", Caller) { MaxTotalTokens = 200 }, CancellationToken.None).ToListAsync();

        var failed = events.OfType<TurnFailedEvent>().Should().ContainSingle().Subject;
        failed.Usage.InputTokens.Should().Be(100);
        failed.Usage.OutputTokens.Should().Be(100);
    }

    [Fact]
    public async Task The_budget_belongs_to_the_turn_so_the_next_turn_on_the_session_starts_from_zero()
    {
        var (runtime, scripted, session) = await FiveCallLoopAsync();
        await runtime.RunTurnAsync(new AgentTurnRequest(session, "go", Caller) { MaxTotalTokens = 200 }, CancellationToken.None);

        var second = await runtime.RunTurnAsync(new AgentTurnRequest(session, "again", Caller) { MaxTotalTokens = 200 }, CancellationToken.None);

        scripted.Requests.Should().HaveCount(4);
        second.Error.Message.Should().Be("Stopped before model round trip 3: 200 tokens used of a budget of 200.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_ceiling_that_is_not_positive_fails_validation_before_the_session_is_claimed(int ceiling)
    {
        var (f, session) = await FiveCallLoopFixtureAsync();

        var result = await f.Runtime.RunTurnAsync(new AgentTurnRequest(session, "go", Caller) { MaxTotalTokens = ceiling }, CancellationToken.None);

        result.Error.Code.Should().Be(AgentErrorCode.Validation);
        f.Publisher.Of<TurnStartedNotification>().Should().BeEmpty();
    }

    // ---------- nested turns ----------

    /// <summary>
    /// A tool of the parent turn runs a whole turn on a second session of the same runtime, as a subagent does. The
    /// parent spends 200 of 250 on its first round trip; the child, budgeted at 150, spends 200 on its one round trip;
    /// the parent then makes its second round trip.
    /// </summary>
    [Fact]
    public async Task A_nested_turn_counts_its_own_tokens_and_neither_inherits_nor_adds_to_its_parents()
    {
        var childResults = new List<Result<AgentTurnResult, AgentError>>();
        RuntimeFixture f = null!;
        var spawn = AIFunctionFactory.Create(
            async (string task, CancellationToken ct) =>
            {
                var child = (await f.Runtime.CreateSessionAsync(f.Agent.Id, Caller, ct)).Value;
                childResults.Add(await f.Runtime.RunTurnAsync(new AgentTurnRequest(child, task, Caller) { MaxTotalTokens = 150 }, ct));
                return "spawned";
            },
            "spawn");
        f = new RuntimeFixture().WithTool(spawn).Build();
        f.Client
            .ThenToolCall("t__spawn", new { task = "sub" }, input: 100, output: 100)   // parent round trip 1: parent at 200
            .ThenText("child done", input: 100, output: 100)                          // child round trip 1: child at 200
            .ThenText("parent done", input: 10, output: 10);                          // parent round trip 2
        var parent = (await f.Runtime.CreateSessionAsync(f.Agent.Id, Caller, CancellationToken.None)).Value;

        var result = await f.Runtime.RunTurnAsync(new AgentTurnRequest(parent, "go", Caller) { MaxTotalTokens = 250 }, CancellationToken.None);

        childResults.Should().ContainSingle().Which.IsSuccess.Should().BeTrue();
        result.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// A child turn with no budget of its own runs unbounded inside a budgeted parent: it spends 400, above the
    /// parent's 250, and still makes its second round trip.
    /// </summary>
    [Fact]
    public async Task A_nested_turn_without_a_budget_of_its_own_does_not_take_its_parents_ceiling()
    {
        var childResults = new List<Result<AgentTurnResult, AgentError>>();
        RuntimeFixture f = null!;
        var spawn = AIFunctionFactory.Create(
            async (string task, CancellationToken ct) =>
            {
                var child = (await f.Runtime.CreateSessionAsync(f.Agent.Id, Caller, ct)).Value;
                childResults.Add(await f.Runtime.RunTurnAsync(new AgentTurnRequest(child, task, Caller), ct));
                return "spawned";
            },
            "spawn");
        f = new RuntimeFixture().WithTool(spawn).WithTool(Echo()).Build();
        f.Client
            .ThenToolCall("t__spawn", new { task = "sub" }, input: 10, output: 10)    // parent round trip 1: parent at 20
            .ThenToolCall("t__echo", new { text = "x" }, input: 200, output: 200)     // child round trip 1: child at 400
            .ThenText("child done", input: 1, output: 1)                             // child round trip 2
            .ThenText("parent done", input: 1, output: 1);                           // parent round trip 2
        var parent = (await f.Runtime.CreateSessionAsync(f.Agent.Id, Caller, CancellationToken.None)).Value;

        var result = await f.Runtime.RunTurnAsync(new AgentTurnRequest(parent, "go", Caller) { MaxTotalTokens = 250 }, CancellationToken.None);

        childResults.Should().ContainSingle().Which.IsSuccess.Should().BeTrue();
        result.IsSuccess.Should().BeTrue();
    }

    // ---------- the client directly ----------

    [Fact]
    public async Task The_non_streaming_path_counts_usage_and_stops_before_the_round_trip_at_the_ceiling()
    {
        var scripted = new ScriptedChatClient().ThenText("a", 100, 100).ThenText("b", 100, 100).ThenText("c", 100, 100);
        using var client = new TurnBudgetChatClient(scripted);
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), Caller, maxTotalTokens: 300);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")]);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")]);
        var third = () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")]);

        (await third.Should().ThrowAsync<AgentTurnException>()).Which.Error
            .Should().Be(AgentError.SubagentBudgetExceeded(300, 3, 400));
        scripted.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_streaming_path_counts_usage_and_stops_before_the_round_trip_at_the_ceiling()
    {
        var scripted = new ScriptedChatClient().ThenText("a", 100, 100).ThenText("b", 100, 100).ThenText("c", 100, 100);
        using var client = new TurnBudgetChatClient(scripted);
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), Caller, maxTotalTokens: 300);

        await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "q")]).ToListAsync();
        await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "q")]).ToListAsync();
        var third = async () => await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "q")]).ToListAsync();

        (await third.Should().ThrowAsync<AgentTurnException>()).Which.Error
            .Should().Be(AgentError.SubagentBudgetExceeded(300, 3, 400));
        scripted.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Usage_the_provider_does_not_report_counts_as_zero_on_both_paths()
    {
        using var client = new TurnBudgetChatClient(new NoUsageChatClient());
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), Caller, maxTotalTokens: int.MaxValue);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")]);
        await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "q")]).ToListAsync();
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")]);

        scope.TokensSoFar.Should().Be(0);
    }

    /// <summary>
    /// Round trips of one turn issued from as many threads as the machine has, each thread looping over calls that
    /// complete synchronously, so the count and the round-trip number are updated with as little time between
    /// updates as the pipeline allows. A lost update shows up as a total short of the exact expected one.
    /// </summary>
    [Fact]
    public async Task Concurrent_round_trips_of_one_turn_lose_no_tokens_and_no_round_trip_numbers()
    {
        const int perThread = 50_000;
        var threads = Math.Max(4, Environment.ProcessorCount);
        using var client = new TurnBudgetChatClient(new FixedUsageChatClient()); // 7 input + 2 output per call
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), Caller, maxTotalTokens: int.MaxValue);
        ChatMessage[] prompt = [new ChatMessage(ChatRole.User, "q")];

        var workers = Enumerable.Range(0, threads).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < perThread; i++)
            {
                await client.GetResponseAsync(prompt);
            }
        })).ToArray();
        await Task.WhenAll(workers);

        scope.TokensSoFar.Should().Be(threads * perThread * 9L);
        scope.NextRoundTrip().Should().Be((threads * perThread) + 1);
    }

    /// <summary>Thread-safe client that answers at once with one shared response reporting 7 input and 2 output tokens.</summary>
    private sealed class FixedUsageChatClient : IChatClient
    {
        private static readonly Task<ChatResponse> Response = Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
        {
            Usage = new UsageDetails { InputTokenCount = 7, OutputTokenCount = 2 },
        });

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => Response;

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>Client whose responses and streams carry no usage at all.</summary>
    private sealed class NoUsageChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "no usage")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "no usage") { FinishReason = ChatFinishReason.Stop };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
