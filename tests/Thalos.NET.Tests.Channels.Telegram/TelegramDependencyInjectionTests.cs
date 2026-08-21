using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Thalos.Channels.Telegram;

namespace Thalos.Tests.Channels.Telegram;

/// <summary>
/// The DI surface: what <c>AddTelegramChannel</c> puts in the container, that it is idempotent, that
/// <see cref="TelegramOptions"/> are validated through the DI path (not merely by calling
/// <see cref="TelegramOptions.Describe"/> directly), and that the poll and send transports it wires up are
/// genuinely separate <see cref="HttpClient"/>s — the hazard this task exists to close.
/// </summary>
public sealed class TelegramDependencyInjectionTests
{
    private static void Valid(TelegramOptions o)
    {
        o.BotToken = "T";
        o.AllowedUserIds = [7];
        o.PrincipalId = "telegram:marcel";
    }

    /// <summary>A minimal host: <c>AddTelegramChannel</c> alone needs neither a chat-client provider nor a session store to resolve.</summary>
    private static ServiceProvider Build(Action<ThalosBuilder> configure)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddThalos(configure);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddTelegramChannel_registers_exactly_one_source_and_one_adapter_named_telegram()
    {
        using var sp = Build(b => b.AddTelegramChannel(Valid));

        sp.GetServices<IChannelSource>().Should().ContainSingle().Which.ChannelId.Should().Be("telegram");
        sp.GetServices<IChannelAdapter>().Should().ContainSingle().Which.ChannelId.Should().Be("telegram");
        sp.GetServices<IChannelSource>().Single().Should().BeOfType<TelegramChannelSource>();
        sp.GetServices<IChannelAdapter>().Single().Should().BeOfType<TelegramChannelAdapter>();
    }

    [Fact]
    public void AddTelegramChannel_is_idempotent_and_does_not_double_pump()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddThalos(t => t.AddTelegramChannel(Valid).AddTelegramChannel(Valid));

        // A second TelegramChannelSource would fight the first over getUpdates and a second adapter would
        // double-render every event — count on the raw registrations, not just the resolved instances, so a
        // regression that adds a second registration whose factory happens to throw first is still caught.
        services.Count(d => d.ServiceType == typeof(IChannelSource)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(IChannelAdapter)).Should().Be(1);

        using var sp = services.BuildServiceProvider();
        sp.GetServices<IChannelSource>().Should().ContainSingle();
        sp.GetServices<IChannelAdapter>().Should().ContainSingle();
    }

    [Fact]
    public void Blank_bot_token_fails_validation_when_resolved_through_the_container()
    {
        using var sp = Build(b => b.AddTelegramChannel(o =>
        {
            Valid(o);
            o.BotToken = "   ";
        }));

        var act = () => sp.GetRequiredService<IOptions<TelegramOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Thalos:Channels:Telegram*")
            .WithMessage("*BotToken*");
    }

    [Fact]
    public void Empty_allowed_user_ids_fails_validation_when_resolved_through_the_container()
    {
        // The check that keeps a misconfiguration from opening the bot to everyone: an empty allow-list must
        // never be read as "allow everyone".
        using var sp = Build(b => b.AddTelegramChannel(o =>
        {
            Valid(o);
            o.AllowedUserIds = [];
        }));

        var act = () => sp.GetRequiredService<IOptions<TelegramOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Thalos:Channels:Telegram*")
            .WithMessage("*AllowedUserIds*");
    }

    [Fact]
    public void Blank_principal_id_fails_validation_when_resolved_through_the_container()
    {
        using var sp = Build(b => b.AddTelegramChannel(o =>
        {
            Valid(o);
            o.PrincipalId = "   ";
        }));

        var act = () => sp.GetRequiredService<IOptions<TelegramOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*Thalos:Channels:Telegram*")
            .WithMessage("*PrincipalId*");
    }

    /// <summary>
    /// Registered before <c>AddTelegramChannel</c>, so it is the earliest observer there is; proves
    /// <c>ValidateOnStart</c> fires at host start rather than only lazily on the pump's first read of the options.
    /// </summary>
    [Fact]
    public async Task ValidateOnStart_fires_before_any_hosted_service_so_a_misconfigured_host_never_starts()
    {
        var probe = new StartingProbe();
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton<IHostedService>(probe);
            services.AddThalos(t => t.AddTelegramChannel(o =>
            {
                Valid(o);
                o.BotToken = "   ";
            }));
        }).Build();

        var start = async () => await host.StartAsync(CancellationToken.None);

        (await start.Should().ThrowAsync<OptionsValidationException>()).WithMessage("*BotToken*");
        probe.Starting.Should().BeFalse("ValidateOnStart runs before the first hosted service, not on first read of the options");
    }

    [Fact]
    public void Options_bind_from_the_Thalos_Channels_Telegram_configuration_section()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Thalos:Channels:Telegram:BotToken"] = "cfg-token",
            ["Thalos:Channels:Telegram:PrincipalId"] = "telegram:marcel",
            ["Thalos:Channels:Telegram:AllowedUserIds:0"] = "7",
            ["Thalos:Channels:Telegram:PollTimeoutSeconds"] = "35",
        }).Build();

        using var sp = Build(b => b.AddTelegramChannel(configuration));

        var o = sp.GetRequiredService<IOptions<TelegramOptions>>().Value;
        o.BotToken.Should().Be("cfg-token");
        o.PrincipalId.Should().Be("telegram:marcel");
        o.AllowedUserIds.Should().Equal(7);
        o.PollTimeoutSeconds.Should().Be(35);
    }

    /// <summary>
    /// The hazard this task exists to close: the source's transport and the adapter's transport must not be the
    /// same <see cref="HttpClient"/>, and the poll one must carry a materially longer timeout than the send one —
    /// specifically, one that comfortably exceeds the configured <see cref="TelegramOptions.PollTimeoutSeconds"/>
    /// (default 50s). Reading the underlying keyed clients directly, rather than only the source/adapter's
    /// black-box behaviour, is the only way to prove the two transports are genuinely distinct without simulating
    /// an actual multi-minute network hang in a unit test.
    /// </summary>
    [Fact]
    public void Poll_and_send_transports_are_separate_clients_with_different_timeouts()
    {
        using var sp = Build(b => b.AddTelegramChannel(Valid));

        var pollClient = sp.GetRequiredKeyedService<HttpClient>(TelegramThalosBuilderExtensions.PollClientKey);
        var sendClient = sp.GetRequiredKeyedService<HttpClient>(TelegramThalosBuilderExtensions.SendClientKey);

        pollClient.Should().NotBeSameAs(sendClient);
        pollClient.Timeout.Should().BeGreaterThan(TimeSpan.FromSeconds(50), "the default PollTimeoutSeconds is 50; the poll client must comfortably outlast Telegram's long-poll hold");
        sendClient.Timeout.Should().BeLessThan(pollClient.Timeout);
        sendClient.Timeout.Should().BeLessThan(TimeSpan.FromSeconds(50), "a hung sendMessage must not hold a conversation's delivery gate for anywhere near as long as a poll");
    }

    /// <summary>The poll client's timeout tracks a non-default configured <c>PollTimeoutSeconds</c>, not a value hardcoded against the 50s default.</summary>
    [Fact]
    public void Poll_client_timeout_tracks_a_configured_non_default_PollTimeoutSeconds()
    {
        using var sp = Build(b => b.AddTelegramChannel(o =>
        {
            Valid(o);
            o.PollTimeoutSeconds = 10;
        }));

        var pollClient = sp.GetRequiredKeyedService<HttpClient>(TelegramThalosBuilderExtensions.PollClientKey);

        // A hardcoded-against-50 implementation would report far more headroom than this; a broken implementation
        // that ignored PollTimeoutSeconds entirely would report the same timeout as the 50s-default case, which
        // this asserts against directly rather than merely asserting ">10s".
        pollClient.Timeout.Should().BeGreaterThan(TimeSpan.FromSeconds(10)).And.BeLessThan(TimeSpan.FromSeconds(50));
    }

    /// <summary>Records whether the host got as far as starting a hosted service; registered before the channel, so it is the earliest observer there is.</summary>
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
}
