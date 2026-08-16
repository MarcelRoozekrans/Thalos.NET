using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Thalos.Runtime;
using Thalos.Sessions;
using Thalos.Testing;
using Thalos.Tests.Unit.Tools;
using Thalos.Tools;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Thalos.Tests.Unit;

public sealed class DependencyInjectionTests
{
    // internal + unique name: the ZeroAlloc.Authorization generator registers every [Policy] type in the assembly and
    // policy names must be unique per compilation (DefaultToolAuthorizerTests already owns "developer").
    [Policy("di-developer")]
    internal sealed class DevPolicy : IAuthorizationPolicy
    {
        public ValueTask<UnitResult<AuthorizationFailure>> EvaluateAsync(ISecurityContext ctx, CancellationToken ct = default) =>
            new(ctx.Roles.Contains("developer") ? UnitResult<AuthorizationFailure>.Success() : UnitResult<AuthorizationFailure>.Failure(new("role", "no")));
    }

    private static ServiceProvider Build(Action<ThalosBuilder>? extra = null)
    {
        var provider = Substitute.For<IChatClientProvider>();
        provider.Name.Returns("fake"); provider.DefaultModel.Returns("m");
        provider.CreateChatClient(Arg.Any<AgentDefinition>()).Returns(new ScriptedChatClient().ThenText("ok"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThalos(thalos =>
        {
            thalos.UseChatClientProvider(provider)
                  .UseInMemorySessionStore()
                  .AddAgent(new AgentDefinition { Id = AgentId.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV", null), Name = "a", Instructions = "i" })
                  .RequireToolPolicy("danger__*", "di-developer")
                  .AddPolicy<DevPolicy>();
            extra?.Invoke(thalos);
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Resolves_runtime_and_runs_a_turn_end_to_end()
    {
        using var sp = Build();
        var runtime = sp.GetRequiredService<IAgentRuntime>();
        var agentId = sp.GetRequiredService<IAgentCatalog>().Agents.Single().Id;
        var caller = new Runtime.TestSecurityContext("u");

        var s = await runtime.CreateSessionAsync(agentId, caller, default);
        var r = await runtime.RunTurnAsync(new AgentTurnRequest(s.Value, "hi", caller), default);
        r.IsSuccess.Should().BeTrue();
        r.Value.Text.Should().Be("ok");
    }

    [Fact]
    public void Session_store_is_wrapped_by_the_telemetry_proxy()
    {
        using var sp = Build();
        sp.GetRequiredService<IAgentSessionStore>().GetType().Name.Should().Be("AgentSessionStoreInstrumented");
    }

    [Fact]
    public void Defaults_are_registered_and_overridable()
    {
        using var sp = Build();
        sp.GetRequiredService<IAgentNotificationPublisher>().Should().BeOfType<NullAgentNotificationPublisher>();
        sp.GetRequiredService<IToolAuthorizer>().Should().BeOfType<DefaultToolAuthorizer>();
        sp.GetRequiredService<AgentEventHub>().Should().NotBeNull();

        using var sp2 = Build(t => t.Services.AddSingleton<IAgentNotificationPublisher, RecordingPublisher>());
        sp2.GetRequiredService<IAgentNotificationPublisher>().Should().BeOfType<RecordingPublisher>();
    }

    [Fact]
    public void Options_bind_tool_policies()
    {
        using var sp = Build();
        var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ThalosOptions>>().Value;
        opts.ToolPolicies.Should().ContainSingle(b => b.ToolPattern == "danger__*" && b.PolicyName == "di-developer");
        opts.Agents.Should().ContainSingle();
    }

    [Fact]
    public void Missing_provider_fails_fast_with_clear_message()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddThalos(t => t.UseInMemorySessionStore());
        using var sp = services.BuildServiceProvider();
        var act = () => sp.GetRequiredService<IAgentRuntime>();
        act.Should().Throw<InvalidOperationException>().WithMessage("*IChatClientProvider*UseAnthropic*");
    }

    [Fact]
    public void Missing_session_store_fails_fast_with_clear_message()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddThalos(t => t.UseChatClientProvider(Substitute.For<IChatClientProvider>()));
        using var sp = services.BuildServiceProvider();
        var act = () => sp.GetRequiredService<IAgentRuntime>();
        act.Should().Throw<InvalidOperationException>().WithMessage("*IAgentSessionStore*UseInMemorySessionStore*");
    }

    [Fact]
    public void Runtime_resolves_without_logging_registered()
    {
        var services = new ServiceCollection(); // no AddLogging()
        services.AddThalos(t => t.UseChatClientProvider(Substitute.For<IChatClientProvider>()).UseInMemorySessionStore());
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IAgentRuntime>().Should().BeOfType<ThalosAgentRuntime>();
    }

    [Fact]
    public void Duplicate_agent_ids_are_rejected_with_both_names()
    {
        var id = AgentId.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV", null);
        using var sp = Build(t => t.AddAgent(new AgentDefinition { Id = id, Name = "b", Instructions = "i" }));

        var act = () => sp.GetRequiredService<IAgentCatalog>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate agent id*'a'*'b'*");
    }

    [Fact]
    public void Custom_session_store_is_wrapped_by_the_telemetry_proxy()
    {
        using var sp = Build(t => t.UseSessionStore<CustomStore>());

        sp.GetRequiredService<IAgentSessionStore>().GetType().Name.Should().Be("AgentSessionStoreInstrumented");
        sp.GetRequiredService<CustomStore>().Should().NotBeNull();
    }

    [Fact]
    public void Repeated_policy_decorator_and_source_registrations_are_deduplicated()
    {
        using var sp = Build(t => t.AddPolicy<DevPolicy>().AddToolSource<EmptySource>().AddToolSource<EmptySource>());

        sp.GetServices<IAuthorizationPolicy>().Should().ContainSingle();
        sp.GetServices<IToolSource>().OfType<EmptySource>().Should().ContainSingle();
    }

    [Fact]
    public async Task Local_tools_are_callable_end_to_end_through_AddThalos()
    {
        var provider = Substitute.For<IChatClientProvider>();
        provider.Name.Returns("fake"); provider.DefaultModel.Returns("m");
        var client = new ScriptedChatClient().ThenToolCall("local__add", new { a = 2, b = 3 }).ThenText("done");
        provider.CreateChatClient(Arg.Any<AgentDefinition>()).Returns(client);

        var services = new ServiceCollection().AddLogging().AddScoped<LocalToolSourceTests.Counter>();
        services.AddThalos(t => t
            .UseChatClientProvider(provider)
            .UseInMemorySessionStore()
            .AddLocalTools("local", typeof(LocalToolSourceTests.MathTools))
            .AddAgent(new AgentDefinition { Id = AgentId.New(), Name = "a", Instructions = "i" }));
        using var sp = services.BuildServiceProvider();
        var runtime = sp.GetRequiredService<IAgentRuntime>();
        var agentId = sp.GetRequiredService<IAgentCatalog>().Agents.Single().Id;
        var caller = new Runtime.TestSecurityContext("u");

        var s = await runtime.CreateSessionAsync(agentId, caller, default);
        var r = await runtime.RunTurnAsync(new AgentTurnRequest(s.Value, "add", caller), default);

        r.IsSuccess.Should().BeTrue();
        r.Value.Text.Should().Be("done");
        r.Value.ToolCalls.Should().ContainSingle(c => c.ToolName == "local__add" && c.Succeeded);
        client.Requests[1].Messages.OfType<ChatMessage>().SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single().Result!.ToString().Should().Be("5");
    }

    [Fact]
    public void AddLocalTools_rejects_unmarked_types_eagerly()
    {
        var services = new ServiceCollection();
        var act = () => services.AddThalos(t => t.AddLocalTools("local", typeof(LocalToolSourceTests.Counter)));

        act.Should().Throw<ArgumentException>().WithMessage("*ThalosToolType*");
    }

    private sealed class CustomStore(TimeProvider clock) : IAgentSessionStore
    {
        private readonly InMemorySessionStore _inner = new(clock);
        public ValueTask<Result<AgentSessionRecord, AgentError>> CreateAsync(AgentId agentId, string ownerId, CancellationToken ct) => _inner.CreateAsync(agentId, ownerId, ct);
        public ValueTask<Result<AgentSessionRecord, AgentError>> GetAsync(SessionId id, CancellationToken ct) => _inner.GetAsync(id, ct);
        public ValueTask<Result<IReadOnlyList<AgentSessionRecord>, AgentError>> ListAsync(string ownerId, int skip, int take, CancellationToken ct) => _inner.ListAsync(ownerId, skip, take, ct);
        public ValueTask<Result<IReadOnlyList<ChatMessage>, AgentError>> LoadMessagesAsync(SessionId id, CancellationToken ct) => _inner.LoadMessagesAsync(id, ct);
        public ValueTask<UnitResult<AgentError>> AppendMessagesAsync(SessionId id, IReadOnlyList<ChatMessage> messages, CancellationToken ct) => _inner.AppendMessagesAsync(id, messages, ct);
        public ValueTask<UnitResult<AgentError>> RecordTurnAsync(SessionId id, TurnUsage usage, CancellationToken ct) => _inner.RecordTurnAsync(id, usage, ct);
        public ValueTask<UnitResult<AgentError>> UpdateStateAsync(SessionId id, SessionState state, CancellationToken ct) => _inner.UpdateStateAsync(id, state, ct);
        public ValueTask<Result<bool, AgentError>> TryTransitionAsync(SessionId id, SessionState from, SessionState target, CancellationToken ct) => _inner.TryTransitionAsync(id, from, target, ct);
    }

    private sealed class EmptySource : IToolSource
    {
        public string Name => "empty";
        public ValueTask<Result<IReadOnlyList<AITool>, AgentError>> GetToolsAsync(CancellationToken ct) => new(Result<IReadOnlyList<AITool>, AgentError>.Success([]));
    }
}
