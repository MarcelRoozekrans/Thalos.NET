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

    private sealed class ThrowingDecorator : IChatClientDecorator
    {
        public int Order => 0;
        public IChatClient Decorate(IChatClient inner, AgentDefinition agent, IServiceProvider services) => throw new InvalidOperationException("decorator broke");
    }

    private sealed class DisposeTrackingClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class Harness
    {
        public ScriptedChatClient Client { get; } = new();
        public DisposeTrackingClient Tracked { get; }
        public IChatClientProvider Provider { get; } = Substitute.For<IChatClientProvider>();
        public IToolCatalog Catalog { get; } = Substitute.For<IToolCatalog>();
        public AgentFactory Factory { get; }

        public Harness(IReadOnlyList<AITool>? tools, params IChatClientDecorator[] decorators)
        {
            Tracked = new DisposeTrackingClient(Client);
            Provider.Name.Returns("fake");
            Provider.DefaultModel.Returns("default-model");
            Provider.CreateChatClient(Arg.Any<AgentDefinition>()).Returns(Tracked);
            Catalog.ResolveAsync(Arg.Any<AgentDefinition>(), Arg.Any<CancellationToken>())
                .Returns(Result<IReadOnlyList<AITool>, AgentError>.Success(tools ?? [AIFunctionFactory.Create(() => "ok", "t")]));
            var services = new ServiceCollection().BuildServiceProvider();
            Factory = new AgentFactory(Provider, decorators, Catalog, new SessionStoreChatHistoryProvider(new InMemorySessionStore(TimeProvider.System)), services, loggerFactory: null);
        }
    }

    private static AgentDefinition Def(string? model = "m1") => new() { Id = AgentId.New(), Name = "a", Instructions = "sys", Model = model };

    private static Harness Build(params IChatClientDecorator[] decorators) => new(tools: null, decorators);

    [Fact]
    public async Task Creates_ChatClientAgent_wired_to_the_history_provider()
    {
        var h = Build();
        var agent = (await h.Factory.GetOrCreateAsync(Def(), default)).Value;

        agent.Should().BeOfType<ChatClientAgent>();
        agent.Name.Should().Be("a");
        ((ChatClientAgent)agent).ChatHistoryProvider.Should().BeOfType<SessionStoreChatHistoryProvider>();
    }

    [Fact]
    public async Task Same_definition_returns_cached_agent_until_invalidated()
    {
        var h = Build();
        var def = Def();
        var a = (await h.Factory.GetOrCreateAsync(def, default)).Value;
        var b = (await h.Factory.GetOrCreateAsync(def, default)).Value;
        a.Should().BeSameAs(b);

        h.Factory.Invalidate(def.Id);
        (await h.Factory.GetOrCreateAsync(def, default)).Value.Should().NotBeSameAs(a);
    }

    [Fact]
    public async Task Concurrent_first_calls_share_one_build()
    {
        var h = Build();
        var def = Def();

        var agents = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(async () => (await h.Factory.GetOrCreateAsync(def, default)).Value)));

        h.Provider.Received(1).CreateChatClient(def);
        agents.Should().AllSatisfy(a => a.Should().BeSameAs(agents[0]));
    }

    [Fact]
    public async Task Invalidate_disposes_the_owned_pipeline()
    {
        var h = Build(new TagDecorator(1, "x", []));
        var def = Def();
        await h.Factory.GetOrCreateAsync(def, default);
        h.Tracked.Disposed.Should().BeFalse();

        h.Factory.Invalidate(def.Id);

        h.Tracked.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Value_equal_definition_instance_reuses_the_cached_agent()
    {
        var h = Build();
        var def = Def();
        var a = (await h.Factory.GetOrCreateAsync(def, default)).Value;

        var b = (await h.Factory.GetOrCreateAsync(def with { }, default)).Value;

        b.Should().BeSameAs(a);
        h.Tracked.Disposed.Should().BeFalse();
        h.Provider.Received(1).CreateChatClient(Arg.Any<AgentDefinition>());
    }

    [Fact]
    public async Task Changed_definition_for_a_cached_id_rebuilds_and_disposes_the_old_pipeline()
    {
        var h = Build();
        var def = Def();
        var a = (await h.Factory.GetOrCreateAsync(def, default)).Value;

        var b = (await h.Factory.GetOrCreateAsync(def with { Instructions = "sys2" }, default)).Value;

        b.Should().NotBeSameAs(a);
        h.Tracked.Disposed.Should().BeTrue();
        h.Provider.Received(2).CreateChatClient(Arg.Any<AgentDefinition>());
    }

    [Fact]
    public async Task Throwing_catalog_yields_ProviderError_and_the_next_call_retries()
    {
        var h = Build();
        var calls = 0;
        h.Catalog.ResolveAsync(Arg.Any<AgentDefinition>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1
                ? throw new InvalidOperationException("catalog exploded")
                : Result<IReadOnlyList<AITool>, AgentError>.Success([]));
        var def = Def();

        var first = await h.Factory.GetOrCreateAsync(def, default);
        first.Error.Code.Should().Be(AgentErrorCode.ProviderError);
        first.Error.Detail.Should().Be("InvalidOperationException", "the raw message is logged, never copied into the error");

        (await h.Factory.GetOrCreateAsync(def, default)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Tool_source_failure_is_not_cached_and_the_next_call_builds()
    {
        // real ToolCatalog over a source that is down once, then healthy
        var h = Build();
        var calls = 0;
        var source = Substitute.For<IToolSource>();
        source.Name.Returns("mcp");
        source.GetToolsAsync(Arg.Any<CancellationToken>()).Returns(_ => ++calls == 1
            ? Result<IReadOnlyList<AITool>, AgentError>.Failure(AgentError.ProviderError("MCP server 'mcp' is unavailable.", "connection refused"))
            : Result<IReadOnlyList<AITool>, AgentError>.Success([AIFunctionFactory.Create(() => "ok", "t")]));
        var catalog = new ToolCatalog([source], Substitute.For<IToolAuthorizer>(), new RecordingNotificationPublisher(), TimeProvider.System);
        var factory = new AgentFactory(h.Provider, [], catalog, new SessionStoreChatHistoryProvider(new InMemorySessionStore(TimeProvider.System)), new ServiceCollection().BuildServiceProvider(), loggerFactory: null);
        var def = Def();

        var first = await factory.GetOrCreateAsync(def, default);
        first.Error.Code.Should().Be(AgentErrorCode.ProviderError);
        first.Error.Message.Should().Be("Tool source 'mcp' is unavailable.");

        var second = await factory.GetOrCreateAsync(def, default);
        second.IsSuccess.Should().BeTrue("the failed build was not cached");
        calls.Should().Be(2);
        h.Client.ThenText("x");
        await ((ChatClientAgent)second.Value).RunAsync("q");
        h.Client.Requests.Single().Options!.Tools.Should().ContainSingle(t => t.Name == "mcp__t");
    }

    [Fact]
    public async Task Dispose_disposes_pipelines_and_rejects_further_calls()
    {
        var h = Build();
        var def = Def();
        await h.Factory.GetOrCreateAsync(def, default);

        h.Factory.Dispose();

        h.Tracked.Disposed.Should().BeTrue();
        var call = async () => await h.Factory.GetOrCreateAsync(def, default);
        await call.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Failed_build_is_not_cached_and_the_next_call_retries()
    {
        var h = Build();
        h.Catalog.ResolveAsync(Arg.Any<AgentDefinition>(), Arg.Any<CancellationToken>())
            .Returns(
                Result<IReadOnlyList<AITool>, AgentError>.Failure(AgentError.ProviderError("mcp down")),
                Result<IReadOnlyList<AITool>, AgentError>.Success([]));
        var def = Def();

        (await h.Factory.GetOrCreateAsync(def, default)).Error.Message.Should().Be("mcp down");
        (await h.Factory.GetOrCreateAsync(def, default)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Throwing_decorator_yields_ProviderError()
    {
        var h = Build(new ThrowingDecorator());

        var result = await h.Factory.GetOrCreateAsync(Def(), default);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.ProviderError);
        result.Error.Detail.Should().Be("InvalidOperationException");
        h.Tracked.Disposed.Should().BeTrue("the partially built pipeline is released");
    }

    [Fact]
    public async Task Decorators_apply_lowest_order_innermost()
    {
        var log = new List<string>();
        var h = Build(new TagDecorator(20, "outer", log), new TagDecorator(10, "inner", log));
        h.Client.ThenText("x");
        var agent = (ChatClientAgent)(await h.Factory.GetOrCreateAsync(Def(), default)).Value;

        // Drive one call through the composed pipeline (agent.ChatClient is MAF's wrappers around the decorated client).
        await agent.ChatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "q")]);

        log.Should().Equal("outer", "inner"); // outermost decorator runs first
    }

    [Fact]
    public async Task Agent_run_sends_model_instructions_and_tools_to_the_provider_client()
    {
        var h = Build();
        h.Client.ThenText("x");
        var agent = (ChatClientAgent)(await h.Factory.GetOrCreateAsync(Def(), default)).Value;
        // The provider in Build() shares no store with a real session; run without a session — MAF allows a null session for one-shot runs.
        await agent.RunAsync("q");

        var opts = h.Client.Requests.Single().Options!;
        opts.ModelId.Should().Be("m1");
        opts.Instructions.Should().Be("sys");
        opts.Tools.Should().ContainSingle(t => t.Name == "t");
    }

    [Fact]
    public async Task Null_model_falls_back_to_the_provider_default()
    {
        var h = Build();
        h.Client.ThenText("x");
        var agent = (ChatClientAgent)(await h.Factory.GetOrCreateAsync(Def(model: null), default)).Value;
        await agent.RunAsync("q");

        h.Client.Requests.Single().Options!.ModelId.Should().Be("default-model");
    }

    [Fact]
    public async Task Empty_tool_set_sends_null_tools()
    {
        var h = new Harness(tools: []);
        h.Client.ThenText("x");
        var agent = (ChatClientAgent)(await h.Factory.GetOrCreateAsync(Def(), default)).Value;
        await agent.RunAsync("q");

        h.Client.Requests[0].Options!.Tools.Should().BeNull();
    }

    private sealed class StaticContextProvider(string instructions) : AIContextProvider
    {
        protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default) =>
            new(new AIContext { Instructions = instructions });
    }

    private sealed class Source(Func<AgentDefinition, AIContextProvider?> create) : IAgentContextProviderSource
    {
        public AIContextProvider? CreateProvider(AgentDefinition agent) => create(agent);
    }

    [Fact]
    public async Task Context_provider_sources_are_consulted_per_agent()
    {
        var h = new Harness(tools: null);
        var factory = new AgentFactory(h.Provider, [], h.Catalog, new SessionStoreChatHistoryProvider(new InMemorySessionStore(TimeProvider.System)), new ServiceCollection().BuildServiceProvider(), null,
            [new Source(a => string.Equals(a.Name, "with", StringComparison.Ordinal) ? new StaticContextProvider("ctx") : null)]);

        var with = (ChatClientAgent)(await factory.GetOrCreateAsync(Def() with { Name = "with" }, default)).Value;
        with.AIContextProviders.Should().NotBeNull().And.ContainSingle().Which.Should().BeOfType<StaticContextProvider>();
        var without = (ChatClientAgent)(await factory.GetOrCreateAsync(Def() with { Name = "without" }, default)).Value;
        (without.AIContextProviders ?? []).Should().BeEmpty();
    }

    [Fact]
    public async Task Changing_memory_settings_rebuilds_the_agent()
    {
        var h = Build();
        var def = Def();
        var a = (await h.Factory.GetOrCreateAsync(def, default)).Value;
        var b = (await h.Factory.GetOrCreateAsync(def with { Memory = new AgentMemorySettings { TopK = 2 } }, default)).Value;
        b.Should().NotBeSameAs(a);
    }
}
