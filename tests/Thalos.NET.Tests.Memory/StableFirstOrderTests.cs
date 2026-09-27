using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Thalos.Caching;
using Thalos.Memory;
using Thalos.Runtime;
using Thalos.Testing;

namespace Thalos.Tests.Memory;

/// <summary>
/// Stable-first prompt order through a real turn: the agent's instructions and the stored history form the prefix a prompt
/// cache can reuse, and the per-turn recall block sits after them, directly before the latest user message. The
/// <see cref="ScriptedChatClient"/> ignores every cache hint, so these facts hold for a provider that caches nothing.
/// </summary>
public sealed class StableFirstOrderTests
{
    private static readonly TestCaller Caller = new("alice");

    private sealed record Harness(ServiceProvider Services, IAgentRuntime Runtime, ScriptedChatClient Scripted, SessionId Session) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    /// <summary>A session with one earlier exchange, then (when <paramref name="seedMemory"/>) one memory the next question recalls.</summary>
    private static async Task<Harness> SessionWithHistoryAsync(bool seedMemory = true, bool promptCaching = false)
    {
        var scripted = new ScriptedChatClient();
        var provider = Substitute.For<IChatClientProvider>();
        provider.Name.Returns("fake");
        provider.DefaultModel.Returns("m");
        provider.CreateChatClient(Arg.Any<AgentDefinition>()).Returns(scripted);
        var agent = new AgentDefinition { Id = AgentId.New(), Name = "a", Instructions = "You are helpful." };
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashedBagOfWordsEmbeddingGenerator());
        services.AddThalos(t =>
        {
            t.UseChatClientProvider(provider).UseInMemorySessionStore().UseMemory(o => o.Recall.MinScore = 0.1).AddAgent(agent);
            if (promptCaching)
            {
                t.UsePromptCaching();
            }
        });
        var sp = services.BuildServiceProvider();
        var runtime = sp.GetRequiredService<IAgentRuntime>();
        var session = (await runtime.CreateSessionAsync(agent.Id, Caller, CancellationToken.None)).Value;

        // the first exchange runs before anything is remembered, so it recalls nothing and stores plainly
        scripted.ThenText("first answer");
        var first = await runtime.RunTurnAsync(new AgentTurnRequest(session, "first question", Caller), CancellationToken.None);
        first.IsSuccess.Should().BeTrue(first.IsFailure ? first.Error.ToString() : "");

        if (seedMemory)
        {
            var remembered = await sp.GetRequiredService<IMemoryService>().RememberAsync(
                new RememberRequest { OwnerId = Caller.Id, Text = "Answer the second question in one line." }, CancellationToken.None);
            remembered.IsSuccess.Should().BeTrue();
        }

        return new Harness(sp, runtime, scripted, session);
    }

    private static Task<Harness> SessionWithHistoryAndOneMemoryAsync() => SessionWithHistoryAsync();

    private static async Task<IReadOnlyList<ChatMessage>> StoredHistoryAsync(Harness h) =>
        (await h.Services.GetRequiredService<IAgentSessionStore>().LoadMessagesAsync(h.Session, CancellationToken.None)).Value;

    private static bool HasHint(ChatMessage m, string key) =>
        m.AdditionalProperties?.TryGetValue(key, out var v) == true && v is true;

    [Fact]
    public async Task Memories_go_after_history_and_the_instructions_stay_stable()
    {
        await using var h = await SessionWithHistoryAndOneMemoryAsync();
        h.Scripted.ThenText("ok");
        await h.Runtime.RunTurnAsync(new AgentTurnRequest(h.Session, "second question", Caller), CancellationToken.None);

        var request = h.Scripted.Requests[^1];
        request.Options!.Instructions.Should().NotContain("<memories", "per-turn recall must not sit inside the cached system prefix");
        request.Messages.Select(m => m.Text).Should().ContainInOrder("first question", "first answer");
        request.Messages[^2].Text.Should().Contain("<memories");
        request.Messages[^1].Text.Should().Be("second question");
    }

    [Fact]
    public async Task The_memories_message_is_not_persisted_into_history()
    {
        await using var h = await SessionWithHistoryAndOneMemoryAsync();
        h.Scripted.ThenText("ok");
        await h.Runtime.RunTurnAsync(new AgentTurnRequest(h.Session, "second question", Caller), CancellationToken.None);

        h.Scripted.Requests[^1].Messages.Should().Contain(m => m.Text.Contains("<memories", StringComparison.Ordinal), "the block reached the model, so its absence below is the filter's work");
        var stored = await StoredHistoryAsync(h);
        stored.Should().NotContain(m => m.Text.Contains("<memories", StringComparison.Ordinal));
        stored.Select(m => m.Text).Should().Equal(["first question", "first answer", "second question", "ok"], "only the transient message is skipped");
    }

    [Fact]
    public async Task With_nothing_recalled_no_message_is_inserted_and_the_order_is_unchanged()
    {
        await using var h = await SessionWithHistoryAsync(seedMemory: false);
        h.Scripted.ThenText("ok");
        await h.Runtime.RunTurnAsync(new AgentTurnRequest(h.Session, "second question", Caller), CancellationToken.None);

        h.Scripted.Requests[^1].Messages.Select(m => m.Text).Should().Equal("first question", "first answer", "second question");
    }

    [Fact]
    public async Task A_tool_round_trip_keeps_exactly_one_memories_message_before_the_user_message()
    {
        await using var h = await SessionWithHistoryAndOneMemoryAsync();
        h.Scripted.ThenToolCall("memory__list", new { }).ThenText("done");
        var result = await h.Runtime.RunTurnAsync(new AgentTurnRequest(h.Session, "second question", Caller), CancellationToken.None);
        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.ToString() : "");

        var second = h.Scripted.Requests[^1].Messages;
        second[^1].Role.Should().Be(ChatRole.Tool, "this is the round trip after the tool call");
        second.Where(m => m.Text.Contains("<memories", StringComparison.Ordinal)).Should().ContainSingle("recall runs once per turn and the block is carried, not re-inserted");
        var memories = second.ToList().FindIndex(m => m.Text.Contains("<memories", StringComparison.Ordinal));
        second[memories + 1].Text.Should().Be("second question");
        (await StoredHistoryAsync(h)).Should().NotContain(m => m.Text.Contains("<memories", StringComparison.Ordinal), "a multi-round-trip turn stores no block either");
    }

    [Fact]
    public async Task With_prompt_caching_the_breakpoint_lands_on_the_users_message_not_the_memories_message()
    {
        await using var h = await SessionWithHistoryAsync(promptCaching: true);
        h.Scripted.ThenText("ok");
        await h.Runtime.RunTurnAsync(new AgentTurnRequest(h.Session, "second question", Caller), CancellationToken.None);

        var messages = h.Scripted.Requests[^1].Messages;
        messages[^1].Text.Should().Be("second question");
        HasHint(messages[^1], PromptCacheHints.Breakpoint).Should().BeTrue();
        messages[^2].Text.Should().Contain("<memories");
        HasHint(messages[^2], PromptCacheHints.Breakpoint).Should().BeFalse();
        HasHint(messages[^2], PromptCacheHints.Transient).Should().BeTrue();
    }
}
