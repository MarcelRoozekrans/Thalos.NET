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
    public RecordingNotificationPublisher Publisher { get; } = new();
    public AgentEventHub Hub { get; } = new();
    public List<AITool> Tools { get; } = [];
    public IToolAuthorizer Authorizer { get; set; }
    public AgentDefinition Agent { get; private set; }
    public ThalosAgentRuntime Runtime { get; private set; } = null!;

    /// <summary>Wraps the store the runtime (and history provider) sees; <see cref="Store"/> stays the raw in-memory store for assertions.</summary>
    public Func<IAgentSessionStore, IAgentSessionStore>? StoreDecorator { get; set; }

    /// <summary>Wraps the publisher the runtime sees; <see cref="Publisher"/> keeps recording underneath.</summary>
    public Func<IAgentNotificationPublisher, IAgentNotificationPublisher>? PublisherDecorator { get; set; }

    /// <summary>What the provider returns from <c>CreateChatClient</c>; defaults to <see cref="Client"/>. May throw.</summary>
    public Func<AgentDefinition, IChatClient>? ChatClientFactory { get; set; }

    public RuntimeFixture(params string[] allowTools)
    {
        Agent = new AgentDefinition { Id = AgentId.New(), Name = "a", Instructions = "sys", Model = "m1", Tools = allowTools.Length == 0 ? ["*"] : allowTools };
        Authorizer = Substitute.For<IToolAuthorizer>();
        Authorizer.AuthorizeAsync(default!, default!, default, default).ReturnsForAnyArgs(ToolAuthorizationDecision.Allow());
    }

    public RuntimeFixture WithTool(AIFunction fn) { Tools.Add(fn); return this; }

    public RuntimeFixture WithModel(string? model) { Agent = Agent with { Model = model }; return this; }

    public RuntimeFixture Build()
    {
        var provider = Substitute.For<IChatClientProvider>();
        provider.Name.Returns("fake"); provider.DefaultModel.Returns("dm");
        provider.CreateChatClient(Arg.Any<AgentDefinition>()).Returns(ci => (ChatClientFactory ?? (_ => Client))(ci.Arg<AgentDefinition>()));

        var source = Substitute.For<IToolSource>();
        source.Name.Returns("t");
        source.GetToolsAsync(Arg.Any<CancellationToken>()).Returns(_ => Result<IReadOnlyList<AITool>, AgentError>.Success(Tools));

        var store = StoreDecorator?.Invoke(Store) ?? Store;
        var publisher = PublisherDecorator?.Invoke(Publisher) ?? Publisher;
        var catalog = new ToolCatalog([source], Authorizer, publisher, TimeProvider.System);
        var history = new SessionStoreChatHistoryProvider(store);
        var services = new ServiceCollection().BuildServiceProvider();
        var factory = new AgentFactory(provider, [], catalog, history, services, null);
        var agents = new StaticAgentCatalog([Agent]);
        Runtime = new ThalosAgentRuntime(agents, factory, store, history, publisher, Hub, TimeProvider.System, null);
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

/// <summary>Store decorator whose <c>RecordTurnAsync</c>/<c>UpdateStateAsync</c> can be overridden to return a failure or throw; everything else delegates.</summary>
internal sealed class FaultingSessionStore(IAgentSessionStore inner) : IAgentSessionStore
{
    public Func<UnitResult<AgentError>>? OnRecordTurn { get; set; }
    public Func<SessionState, UnitResult<AgentError>>? OnUpdateState { get; set; }

    public ValueTask<Result<AgentSessionRecord, AgentError>> CreateAsync(AgentId agentId, string ownerId, CancellationToken ct) => inner.CreateAsync(agentId, ownerId, ct);
    public ValueTask<Result<AgentSessionRecord, AgentError>> GetAsync(SessionId id, CancellationToken ct) => inner.GetAsync(id, ct);
    public ValueTask<Result<IReadOnlyList<AgentSessionRecord>, AgentError>> ListAsync(string ownerId, int skip, int take, CancellationToken ct) => inner.ListAsync(ownerId, skip, take, ct);
    public ValueTask<Result<IReadOnlyList<ChatMessage>, AgentError>> LoadMessagesAsync(SessionId id, CancellationToken ct) => inner.LoadMessagesAsync(id, ct);
    public ValueTask<UnitResult<AgentError>> AppendMessagesAsync(SessionId id, IReadOnlyList<ChatMessage> messages, CancellationToken ct) => inner.AppendMessagesAsync(id, messages, ct);

    public ValueTask<UnitResult<AgentError>> RecordTurnAsync(SessionId id, TurnUsage usage, CancellationToken ct) =>
        OnRecordTurn is { } hook ? new ValueTask<UnitResult<AgentError>>(hook()) : inner.RecordTurnAsync(id, usage, ct);

    public ValueTask<UnitResult<AgentError>> UpdateStateAsync(SessionId id, SessionState state, CancellationToken ct) =>
        OnUpdateState is { } hook ? new ValueTask<UnitResult<AgentError>>(hook(state)) : inner.UpdateStateAsync(id, state, ct);

    public ValueTask<Result<bool, AgentError>> TryTransitionAsync(SessionId id, SessionState from, SessionState target, CancellationToken ct) => inner.TryTransitionAsync(id, from, target, ct);
}

/// <summary>Publisher decorator that throws for notifications of <typeparamref name="TThrowOn"/> and delegates the rest.</summary>
internal sealed class ThrowingPublisher<TThrowOn>(IAgentNotificationPublisher inner) : IAgentNotificationPublisher
{
    public ValueTask PublishAsync<TNotification>(TNotification notification, CancellationToken ct) where TNotification : ZeroAlloc.Mediator.INotification =>
        notification is TThrowOn
            ? throw new InvalidOperationException($"publisher down for {typeof(TThrowOn).Name}")
            : inner.PublishAsync(notification, ct);
}

/// <summary>Stateless, thread-safe chat client: replies <c>echo:{last user text}</c> with fixed usage — for concurrency tests where a shared script would be racy.</summary>
internal sealed class EchoChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var last = messages.Last(m => m.Role == ChatRole.User).Text;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "echo:" + last))
        {
            ModelId = "echo-model",
            Usage = new UsageDetails { InputTokenCount = 7, OutputTokenCount = 2, TotalTokenCount = 9 },
            FinishReason = ChatFinishReason.Stop,
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        await Task.Yield(); // hop threads so the two turns really interleave
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text) { ModelId = "echo-model" };
        yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(response.Usage!)]) { ModelId = "echo-model", FinishReason = ChatFinishReason.Stop };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}
