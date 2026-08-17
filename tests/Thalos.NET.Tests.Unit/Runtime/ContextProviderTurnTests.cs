using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Thalos.Runtime;
using Thalos.Sessions;
using Thalos.Testing;
using Thalos.Tools;
using ZeroAlloc.Results;

namespace Thalos.Tests.Unit.Runtime;

public sealed class ContextProviderTurnTests
{
    private sealed class StaticContextProvider : AIContextProvider
    {
        protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default) =>
            new(new AIContext { Instructions = "CTX-MARKER" });
    }

    private sealed class Source : IAgentContextProviderSource
    {
        public AIContextProvider? CreateProvider(AgentDefinition agent) => new StaticContextProvider();
    }

    /// <summary>MAF may deliver AIContext.Instructions as ChatOptions.Instructions (what 1.17.0 does) or as a system message; accept both.</summary>
    internal static string AllInstructions((IReadOnlyList<ChatMessage> Messages, ChatOptions? Options) request) =>
        (request.Options?.Instructions ?? "") + "\n" + string.Join('\n', request.Messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text));

    [Fact]
    public async Task Provider_instructions_reach_the_chat_client()
    {
        var client = new ScriptedChatClient().ThenText("ok");
        var provider = Substitute.For<IChatClientProvider>();
        provider.Name.Returns("fake"); provider.DefaultModel.Returns("m");
        provider.CreateChatClient(Arg.Any<AgentDefinition>()).Returns(client);
        var catalog = Substitute.For<IToolCatalog>();
        catalog.ResolveAsync(Arg.Any<AgentDefinition>(), Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<AITool>, AgentError>.Success([]));
        var store = new InMemorySessionStore(TimeProvider.System);
        var history = new SessionStoreChatHistoryProvider(store);
        var factory = new AgentFactory(provider, [], catalog, history, new ServiceCollection().BuildServiceProvider(), null, [new Source()]);
        var def = new AgentDefinition { Id = AgentId.New(), Name = "a", Instructions = "sys" };
        var runtime = new ThalosAgentRuntime(new StaticAgentCatalog([def]), factory, store, history, new RecordingNotificationPublisher(), new AgentEventHub(), TimeProvider.System, null);
        var caller = RuntimeFixture.User();

        var s = (await runtime.CreateSessionAsync(def.Id, caller, default)).Value;
        var r = await runtime.RunTurnAsync(new AgentTurnRequest(s, "hi", caller), default);

        r.IsSuccess.Should().BeTrue(r.IsFailure ? r.Error.ToString() : "");
        AllInstructions(client.Requests.Single()).Should().Contain("CTX-MARKER").And.Contain("sys");
    }
}
