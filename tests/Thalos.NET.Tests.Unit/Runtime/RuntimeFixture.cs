using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Thalos.Runtime;
using Thalos.Sessions;
using Thalos.Testing;
using Thalos.Tools;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos.Tests.Unit.Runtime;

/// <summary>Everything wired with real core classes; only the LLM (ScriptedChatClient) and tool sources are fakes.</summary>
internal sealed class RuntimeFixture
{
    public ScriptedChatClient Client { get; } = new();
    public InMemorySessionStore Store { get; } = new(TimeProvider.System);
    public RecordingPublisher Publisher { get; } = new();
    public AgentEventHub Hub { get; } = new();
    public List<AITool> Tools { get; } = [];
    public IToolAuthorizer Authorizer { get; set; }
    public AgentDefinition Agent { get; }
    public ThalosAgentRuntime Runtime { get; private set; } = null!;

    public RuntimeFixture(params string[] allowTools)
    {
        Agent = new AgentDefinition { Id = AgentId.New(), Name = "a", Instructions = "sys", Model = "m1", Tools = allowTools.Length == 0 ? ["*"] : allowTools };
        Authorizer = Substitute.For<IToolAuthorizer>();
        Authorizer.AuthorizeAsync(default!, default!, default, default).ReturnsForAnyArgs(ToolAuthorizationDecision.Allow());
    }

    public RuntimeFixture WithTool(AIFunction fn) { Tools.Add(fn); return this; }

    public RuntimeFixture Build()
    {
        var provider = Substitute.For<IChatClientProvider>();
        provider.Name.Returns("fake"); provider.DefaultModel.Returns("dm");
        provider.CreateChatClient(Arg.Any<AgentDefinition>()).Returns(Client);

        var source = Substitute.For<IToolSource>();
        source.Name.Returns("t");
        source.GetToolsAsync(Arg.Any<CancellationToken>()).Returns(_ => Result<IReadOnlyList<AITool>, AgentError>.Success(Tools));

        var catalog = new ToolCatalog([source], Authorizer, Publisher, TimeProvider.System);
        var history = new SessionStoreChatHistoryProvider(Store);
        var services = new ServiceCollection().BuildServiceProvider();
        var factory = new AgentFactory(provider, [], catalog, history, services, null);
        var agents = new StaticAgentCatalog([Agent]);
        Runtime = new ThalosAgentRuntime(agents, factory, Store, history, Publisher, Hub, TimeProvider.System, null);
        return this;
    }

    public static ISecurityContext User(string id = "u1", params string[] roles) => new TestSecurityContext(id, roles);
}

internal sealed class StaticAgentCatalog(IReadOnlyList<AgentDefinition> agents) : IAgentCatalog
{
    public IReadOnlyList<AgentDefinition> Agents => agents;
    public bool TryGet(AgentId id, [MaybeNullWhen(false)] out AgentDefinition definition)
    {
        definition = agents.FirstOrDefault(a => a.Id == id)!;
        return definition is not null;
    }
}
