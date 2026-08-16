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

public sealed class AgentFactoryTests
{
    private sealed class TagDecorator(int order, string tag, List<string> log) : IChatClientDecorator
    {
        public int Order => order;
        public IChatClient Decorate(IChatClient inner, AgentDefinition agent, IServiceProvider services) =>
            new ChatClientBuilder(inner).Use(async (msgs, opts, next, ct) => { log.Add(tag); await next(msgs, opts, ct); }).Build(services);
    }

    private static AgentDefinition Def(params string[] tools) => new() { Id = AgentId.New(), Name = "a", Instructions = "sys", Model = "m1", Tools = tools.Length == 0 ? ["*"] : tools };

    private static (AgentFactory factory, ScriptedChatClient client, List<string> log) Build(params IChatClientDecorator[] decorators)
    {
        var client = new ScriptedChatClient();
        var provider = Substitute.For<IChatClientProvider>();
        provider.Name.Returns("fake"); provider.DefaultModel.Returns("default-model");
        provider.CreateChatClient(Arg.Any<AgentDefinition>()).Returns(client);

        var catalog = Substitute.For<IToolCatalog>();
        catalog.ResolveAsync(Arg.Any<AgentDefinition>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<AITool>, AgentError>.Success((IReadOnlyList<AITool>)[AIFunctionFactory.Create(() => "ok", "t")]));

        var log = new List<string>();
        var services = new ServiceCollection().BuildServiceProvider();
        var factory = new AgentFactory(provider, decorators, catalog, new SessionStoreChatHistoryProvider(new InMemorySessionStore(TimeProvider.System)), services, loggerFactory: null);
        return (factory, client, log);
    }

    [Fact]
    public async Task Creates_ChatClientAgent_wired_to_the_history_provider()
    {
        var (factory, _, _) = Build();
        var agent = (await factory.GetOrCreateAsync(Def(), default)).Value;

        agent.Should().BeOfType<ChatClientAgent>();
        agent.Name.Should().Be("a");
        ((ChatClientAgent)agent).ChatHistoryProvider.Should().BeOfType<SessionStoreChatHistoryProvider>();
    }

    [Fact]
    public async Task Same_definition_returns_cached_agent_until_invalidated()
    {
        var (factory, _, _) = Build();
        var def = Def();
        var a = (await factory.GetOrCreateAsync(def, default)).Value;
        var b = (await factory.GetOrCreateAsync(def, default)).Value;
        a.Should().BeSameAs(b);

        factory.Invalidate(def.Id);
        (await factory.GetOrCreateAsync(def, default)).Value.Should().NotBeSameAs(a);
    }

    [Fact]
    public async Task Decorators_apply_lowest_order_innermost()
    {
        var log = new List<string>();
        var (factory, client, _) = Build(new TagDecorator(20, "outer", log), new TagDecorator(10, "inner", log));
        client.ThenText("x");
        var agent = (ChatClientAgent)(await factory.GetOrCreateAsync(Def(), default)).Value;

        // Drive one call through the composed pipeline (agent.ChatClient is the decorated client, before MAF's FIC).
        await agent.ChatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "q")]);

        log.Should().Equal("outer", "inner"); // outermost decorator runs first
    }

    [Fact]
    public async Task Agent_run_sends_model_instructions_and_tools_to_the_provider_client()
    {
        var (factory, client, _) = Build();
        client.ThenText("x");
        var agent = (ChatClientAgent)(await factory.GetOrCreateAsync(Def(), default)).Value;
        // The provider in Build() shares no store with a real session; run without a session — MAF allows a null session for one-shot runs.
        await agent.RunAsync("q");

        var opts = client.Requests.Single().Options!;
        opts.ModelId.Should().Be("m1");
        opts.Instructions.Should().Be("sys");
        opts.Tools.Should().ContainSingle(t => t.Name == "t");
    }
}
