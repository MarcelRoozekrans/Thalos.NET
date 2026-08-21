using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Thalos.Channels;
using Thalos.Channels.Console;
using ZeroAlloc.Results;

namespace Thalos.Tests.Channels;

/// <summary>
/// The DI surface: what <c>UseChannels</c> puts in the container, what <c>UseConversationMap</c> replaces (in
/// either call order), how <see cref="ChannelOptions"/> are validated, and what <c>AddConsoleChannel</c> registers.
/// </summary>
public sealed class ChannelDependencyInjectionTests
{
    private static IChatClientProvider Provider()
    {
        var provider = Substitute.For<IChatClientProvider>();
        provider.Name.Returns("fake");
        provider.DefaultModel.Returns("m");
        return provider;
    }

    /// <summary>
    /// A real host wired the way production wires it: a chat-client provider and a session store, both required
    /// before <see cref="IAgentRuntime"/> — and therefore <see cref="ChannelPump"/>, which takes it directly —
    /// resolves at all.
    /// </summary>
    private static ServiceProvider Build(Action<ThalosBuilder> configure)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddThalos(t =>
        {
            t.UseChatClientProvider(Provider()).UseInMemorySessionStore();
            configure(t);
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public void UseChannels_registers_the_pump_and_the_default_map()
    {
        using var sp = Build(b => b.UseChannels(o => o.DefaultAgent = "daedalus"));

        sp.GetServices<IHostedService>().OfType<ChannelPump>().Should().ContainSingle();
        sp.GetRequiredService<IConversationMap>().Should().BeOfType<InMemoryConversationMap>();
    }

    [Fact]
    public void UseChannels_is_idempotent_and_the_last_configure_wins()
    {
        using var sp = Build(b => b.UseChannels(o => o.DefaultAgent = "a").UseChannels(o => o.DefaultAgent = "b"));

        sp.GetServices<IHostedService>().OfType<ChannelPump>().Should().ContainSingle();
        sp.GetRequiredService<IOptions<ChannelOptions>>().Value.DefaultAgent.Should().Be("b");
    }

    [Fact]
    public void UseConversationMap_replaces_the_default_when_called_before_UseChannels()
    {
        using var sp = Build(b => b.UseConversationMap<FakeConversationMap>().UseChannels(o => o.DefaultAgent = "a"));

        sp.GetRequiredService<IConversationMap>().Should().BeOfType<FakeConversationMap>();
    }

    [Fact]
    public void UseConversationMap_replaces_the_default_when_called_after_UseChannels()
    {
        using var sp = Build(b => b.UseChannels(o => o.DefaultAgent = "a").UseConversationMap<FakeConversationMap>());

        sp.GetRequiredService<IConversationMap>().Should().BeOfType<FakeConversationMap>();
    }

    [Fact]
    public void Invalid_options_fail_validation_rather_than_starting_a_broken_pump()
    {
        using var sp = Build(b => b.UseChannels(o => o.DefaultAgent = "   "));

        var act = () => sp.GetRequiredService<IOptions<ChannelOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Thalos:Channels:*")
            .WithMessage("*DefaultAgent*");
    }

    /// <summary>
    /// Registered before <c>UseChannels</c>, so it is the earliest observer there is; proves <c>ValidateOnStart</c>
    /// fires before the pump — or any other hosted service — gets a chance to start against broken options.
    /// </summary>
    [Fact]
    public async Task ValidateOnStart_fires_before_any_hosted_service_so_a_misconfigured_host_never_starts()
    {
        var probe = new StartingProbe();
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton<IHostedService>(probe);
            services.AddThalos(t => t.UseChatClientProvider(Provider()).UseInMemorySessionStore().UseChannels(o => o.DefaultAgent = "   "));
        }).Build();

        var start = async () => await host.StartAsync(CancellationToken.None);

        (await start.Should().ThrowAsync<OptionsValidationException>()).WithMessage("*DefaultAgent*");
        probe.Starting.Should().BeFalse("ValidateOnStart runs before the first hosted service, not on the pump's first read of the options");
    }

    [Fact]
    public void Options_bind_from_the_Thalos_Channels_configuration_section()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Thalos:Channels:DefaultAgent"] = "daedalus",
            ["Thalos:Channels:Enabled"] = "false",
            ["Thalos:Channels:IdleTimeout"] = "01:00:00",
            ["Thalos:Channels:FlushInterval"] = "00:00:02",
        }).Build();

        using var sp = Build(b => b.UseChannels(configuration));

        var o = sp.GetRequiredService<IOptions<ChannelOptions>>().Value;
        o.DefaultAgent.Should().Be("daedalus");
        o.Enabled.Should().BeFalse();
        o.IdleTimeout.Should().Be(TimeSpan.FromHours(1));
        o.FlushInterval.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void AddConsoleChannel_registers_a_source_and_adapter_over_the_real_console_and_is_idempotent()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddThalos(t => t.AddConsoleChannel().AddConsoleChannel());

        services.Count(d => d.ServiceType == typeof(IChannelSource)).Should().Be(1, "TryAddEnumerable must not double-pump the console");
        services.Count(d => d.ServiceType == typeof(IChannelAdapter)).Should().Be(1);

        using var sp = services.BuildServiceProvider();
        sp.GetServices<IChannelSource>().Should().ContainSingle().Which.Should().BeOfType<ConsoleChannelSource>();
        sp.GetServices<IChannelAdapter>().Should().ContainSingle().Which.Should().BeOfType<ConsoleChannelAdapter>();
        sp.GetServices<IChannelSource>().Single().ChannelId.Should().Be("console");
        sp.GetServices<IChannelAdapter>().Single().ChannelId.Should().Be("console");
    }

    /// <summary>Records whether the host got as far as starting a hosted service; registered before channels, so it is the earliest observer there is.</summary>
    private sealed class StartingProbe : IHostedLifecycleService
    {
        public bool Starting { get; private set; }

        public Task StartingAsync(CancellationToken cancellationToken)
        {
            Starting = true;
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>A distinguishable stand-in for <see cref="InMemoryConversationMap"/>, so a replacement test proves the swap actually happened.</summary>
    private sealed class FakeConversationMap : IConversationMap
    {
        public ValueTask<Result<ConversationBinding?, AgentError>> GetAsync(string channelId, ConversationId conversationId, CancellationToken ct) =>
            new(Result<ConversationBinding?, AgentError>.Success(null));

        public ValueTask<UnitResult<AgentError>> BindAsync(ConversationBinding binding, CancellationToken ct) =>
            new(UnitResult<AgentError>.Success());

        public ValueTask<UnitResult<AgentError>> UnbindAsync(string channelId, ConversationId conversationId, CancellationToken ct) =>
            new(UnitResult<AgentError>.Success());
    }
}
