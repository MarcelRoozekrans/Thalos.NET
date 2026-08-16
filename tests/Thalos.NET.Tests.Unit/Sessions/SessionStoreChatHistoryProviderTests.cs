using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NSubstitute;
using Thalos.Sessions;
using Thalos.Testing;
using ZeroAlloc.Results;

namespace Thalos.Tests.Unit.Sessions;

public sealed class SessionStoreChatHistoryProviderTests
{
    private static (ChatClientAgent agent, InMemorySessionStore store, SessionStoreChatHistoryProvider provider) Build(ScriptedChatClient client, params AITool[] tools)
    {
        var store = new InMemorySessionStore(TimeProvider.System);
        var provider = new SessionStoreChatHistoryProvider(store);
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Name = "t",
            ChatHistoryProvider = provider,
            ChatOptions = new ChatOptions { Instructions = "sys", Tools = tools.Length == 0 ? null : tools },
        });
        return (agent, store, provider);
    }

    [Fact]
    public async Task Text_turn_stores_user_and_assistant_messages_and_replays_them_next_turn()
    {
        var client = new ScriptedChatClient().ThenText("hello Alice").ThenText("you said hi");
        var (agent, store, provider) = Build(client);
        var sessionId = (await store.CreateAsync(AgentId.New(), "o", default)).Value.Id;

        var maf1 = await provider.CreateBoundSessionAsync(agent, sessionId, default);
        (await agent.RunAsync("hi", maf1)).Text.Should().Be("hello Alice");

        var stored = (await store.LoadMessagesAsync(sessionId, default)).Value;
        stored.Select(m => (m.Role, m.Text)).Should().Equal((ChatRole.User, "hi"), (ChatRole.Assistant, "hello Alice"));

        // second turn on a *fresh* MAF session bound to the same Thalos session → history is replayed to the model
        var maf2 = await provider.CreateBoundSessionAsync(agent, sessionId, default);
        await agent.RunAsync("again", maf2);
        var lastRequest = client.Requests[^1].Messages;
        lastRequest.Select(m => m.Text).Should().ContainInOrder("hi", "hello Alice", "again");
    }

    [Fact]
    public async Task Tool_round_trip_is_stored_as_four_messages()
    {
        var echo = AIFunctionFactory.Create((string text) => "echo:" + text, "echo");
        var client = new ScriptedChatClient().ThenToolCall("echo", new { text = "x" }).ThenText("done");
        var (agent, store, provider) = Build(client, echo);
        var sessionId = (await store.CreateAsync(AgentId.New(), "o", default)).Value.Id;
        var maf = await provider.CreateBoundSessionAsync(agent, sessionId, default);

        (await agent.RunAsync("go", maf)).Text.Should().Be("done");

        var stored = (await store.LoadMessagesAsync(sessionId, default)).Value;
        stored.Select(m => m.Role).Should().Equal(ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant);
        stored[1].Contents.OfType<FunctionCallContent>().Should().ContainSingle();
        stored[2].Contents.OfType<FunctionResultContent>().Should().ContainSingle();
    }

    [Fact]
    public async Task Unbound_session_runs_statelessly_and_stores_nothing()
    {
        var (agent, store, _) = Build(new ScriptedChatClient().ThenText("x"));
        var response = await agent.RunAsync("hi", await agent.CreateSessionAsync());
        response.Text.Should().Be("x");
        // nothing was created in the store
        (await store.ListAsync("o", 0, 10, default)).Value.Should().BeEmpty();
    }

    [Fact]
    public void StateKeys_exposes_the_binding_key()
    {
        new SessionStoreChatHistoryProvider(new InMemorySessionStore(TimeProvider.System)).StateKeys.Should().Equal(SessionStoreChatHistoryProvider.StateKey);
    }

    [Fact]
    public async Task Corrupt_session_binding_fails_the_turn_instead_of_running_unbound()
    {
        var (agent, _, _) = Build(new ScriptedChatClient().ThenText("x"));
        var session = await agent.CreateSessionAsync();
        session.StateBag.SetValue(SessionStoreChatHistoryProvider.StateKey, "not-a-session-id");

        var run = async () => await agent.RunAsync("hi", session);

        var ex = (await run.Should().ThrowAsync<AgentTurnException>()).Which;
        ex.Error.Code.Should().Be(AgentErrorCode.StoreError);
        ex.Error.Detail.Should().Be("not-a-session-id");
    }

    [Fact]
    public async Task Store_load_failure_surfaces_from_RunAsync_as_AgentTurnException()
    {
        var store = Substitute.For<IAgentSessionStore>();
        store.LoadMessagesAsync(Arg.Any<SessionId>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<ChatMessage>, AgentError>.Failure(AgentError.StoreError("db down")));
        var provider = new SessionStoreChatHistoryProvider(store);
        var agent = new ChatClientAgent(new ScriptedChatClient().ThenText("x"), new ChatClientAgentOptions { Name = "t", ChatHistoryProvider = provider });
        var session = await provider.CreateBoundSessionAsync(agent, SessionId.New(), default);

        var run = async () => await agent.RunAsync("hi", session);

        var ex = (await run.Should().ThrowAsync<AgentTurnException>()).Which;
        ex.Error.Code.Should().Be(AgentErrorCode.StoreError);
        ex.Error.Message.Should().Be("db down");
    }
}
