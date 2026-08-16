using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Thalos.Runtime;
using Thalos.Testing;
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
}
