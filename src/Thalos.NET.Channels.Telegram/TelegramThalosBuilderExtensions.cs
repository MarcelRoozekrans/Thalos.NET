using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Thalos.Channels.Telegram;

/// <summary>Registers the Telegram channel — <see cref="TelegramChannelSource"/> and <see cref="TelegramChannelAdapter"/> — on a <see cref="ThalosBuilder"/>.</summary>
public static class TelegramThalosBuilderExtensions
{
    private const string ApiBaseAddress = "https://api.telegram.org/";

    /// <summary>
    /// DI key for the <see cref="HttpClient"/>/<see cref="TelegramBotClient"/> pair used by <c>getUpdates</c>. Kept
    /// at <see langword="internal"/> visibility (rather than <see langword="private"/>) purely so a DI test can
    /// resolve the same keyed <see cref="HttpClient"/> the source is built over and prove it is a distinct instance
    /// from <see cref="SendClientKey"/>'s, with a materially different <see cref="HttpClient.Timeout"/> — not part
    /// of this package's supported public surface.
    /// </summary>
    internal const string PollClientKey = "Thalos.Channels.Telegram.Poll";

    /// <summary>
    /// DI key for the <see cref="HttpClient"/>/<see cref="TelegramBotClient"/> pair used by <c>sendMessage</c>,
    /// <c>editMessageText</c> and <c>sendChatAction</c>. See <see cref="PollClientKey"/> for why this is <see langword="internal"/>.
    /// </summary>
    internal const string SendClientKey = "Thalos.Channels.Telegram.Send";

    /// <summary>
    /// Added on top of <see cref="TelegramOptions.PollTimeoutSeconds"/> to get the poll <see cref="HttpClient"/>'s
    /// <see cref="HttpClient.Timeout"/>: room for the round trip itself, on top of however long Telegram holds the
    /// long-poll open server-side. At the 50-second default this yields an 80-second client timeout.
    /// </summary>
    private static readonly TimeSpan PollTimeoutMargin = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The send <see cref="HttpClient"/>'s <see cref="HttpClient.Timeout"/>. <c>sendMessage</c>, <c>editMessageText</c>
    /// and <c>sendChatAction</c> are ordinary (non-long-polling) calls that normally complete in well under a second;
    /// 20 seconds is generous headroom for a slow network without holding a conversation's delivery gate anywhere
    /// close to as long as the poll client's timeout — see the remarks on <see cref="AddTelegramChannel(ThalosBuilder, Action{TelegramOptions}?)"/>.
    /// </summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Enables the Telegram channel: <see cref="TelegramOptions"/>, a <see cref="TelegramChannelSource"/> and a
    /// <see cref="TelegramChannelAdapter"/> added to the channel pump via <c>TryAddEnumerable</c> — call
    /// <see cref="Thalos.Channels.ChannelThalosBuilderExtensions.UseChannels(ThalosBuilder, Action{ChannelOptions}?)"/>
    /// (or its <see cref="IConfiguration"/> overload) as well, so something actually pumps the registered source.
    /// Idempotent: every <paramref name="configure"/> runs, last wins, but calling this twice still produces exactly
    /// one source and one adapter.
    /// </summary>
    /// <remarks>
    /// <see cref="TelegramOptions"/> are validated when first resolved and at host start (<c>ValidateOnStart</c>) via
    /// <see cref="TelegramOptions.Describe"/>; a violation throws <see cref="OptionsValidationException"/> — most
    /// importantly for a blank <see cref="TelegramOptions.BotToken"/>, a blank <see cref="TelegramOptions.PrincipalId"/>,
    /// or an empty <see cref="TelegramOptions.AllowedUserIds"/>, which would otherwise leave the bot reachable by
    /// anyone who finds it.
    /// <para>
    /// <b>Two <see cref="HttpClient"/>s, not one.</b> <c>getUpdates</c> long-polls with a server-side hold close to
    /// <see cref="TelegramOptions.PollTimeoutSeconds"/>, so the source's transport needs a timeout comfortably past
    /// that. But <see cref="TelegramChannelAdapter"/> serialises deliveries to one conversation behind a semaphore —
    /// if <c>sendMessage</c> shared that long-timeout client and ever hung, it would hold the gate for the whole
    /// timeout and stall that chat completely. So the source and the adapter are built over two separate keyed
    /// <see cref="HttpClient"/>/<see cref="TelegramBotClient"/> pairs: a long one for polling, a short one for
    /// sending, edited and typing calls.
    /// </para>
    /// </remarks>
    public static ThalosBuilder AddTelegramChannel(this ThalosBuilder builder, Action<TelegramOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = builder.Services.AddOptions<TelegramOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        return Register(builder, options);
    }

    /// <summary>Same as <see cref="AddTelegramChannel(ThalosBuilder, Action{TelegramOptions}?)"/>, options bound from the <see cref="TelegramOptions.SectionName"/> section of <paramref name="configuration"/>.</summary>
    public static ThalosBuilder AddTelegramChannel(this ThalosBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = builder.Services.AddOptions<TelegramOptions>().Bind(configuration.GetSection(TelegramOptions.SectionName));
        return Register(builder, options);
    }

    private static ThalosBuilder Register(ThalosBuilder builder, OptionsBuilder<TelegramOptions> options)
    {
        options.ValidateOnStart();

        var services = builder.Services;
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TelegramOptions>, TelegramOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);

        // Poll pair: HttpClient.Timeout tracks the CONFIGURED PollTimeoutSeconds (not just its default), so a host
        // that raises it does not silently break long polling again.
        // Trade-off, recorded once: both clients below are plain singleton HttpClients (a fixed SocketsHttpHandler
        // for the process's lifetime), not IHttpClientFactory-managed ones — so neither rotates its handler on a
        // DNS change the way AddHttpClient's periodic rotation would. Acceptable for one stable host
        // (api.telegram.org); revisit if that ever stops being true.
        services.TryAddKeyedSingleton<HttpClient>(PollClientKey, (sp, _) => new HttpClient
        {
            BaseAddress = new Uri(ApiBaseAddress),
            Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<TelegramOptions>>().Value.PollTimeoutSeconds) + PollTimeoutMargin,
        });
        services.TryAddKeyedSingleton<TelegramBotClient>(PollClientKey, (sp, _) =>
            new TelegramBotClient(sp.GetRequiredKeyedService<HttpClient>(PollClientKey), sp.GetRequiredService<IOptions<TelegramOptions>>().Value.BotToken));

        // Send pair: short, fixed timeout — see the remarks on AddTelegramChannel for why it must stay separate
        // from the poll pair.
        services.TryAddKeyedSingleton<HttpClient>(SendClientKey, (_, _) => new HttpClient
        {
            BaseAddress = new Uri(ApiBaseAddress),
            Timeout = SendTimeout,
        });
        services.TryAddKeyedSingleton<TelegramBotClient>(SendClientKey, (sp, _) =>
            new TelegramBotClient(sp.GetRequiredKeyedService<HttpClient>(SendClientKey), sp.GetRequiredService<IOptions<TelegramOptions>>().Value.BotToken));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelSource, TelegramChannelSource>(sp =>
            new TelegramChannelSource(
                sp.GetRequiredKeyedService<TelegramBotClient>(PollClientKey),
                sp.GetRequiredService<IOptions<TelegramOptions>>().Value,
                sp.GetRequiredService<ILogger<TelegramChannelSource>>())));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelAdapter, TelegramChannelAdapter>(sp =>
            new TelegramChannelAdapter(
                sp.GetRequiredKeyedService<TelegramBotClient>(SendClientKey),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<TelegramChannelAdapter>>())));

        return builder;
    }

    /// <summary>Runs <see cref="TelegramOptions.Describe"/> when the options are first resolved, and at host start via <c>ValidateOnStart</c>.</summary>
    private sealed class TelegramOptionsValidator : IValidateOptions<TelegramOptions>
    {
        public ValidateOptionsResult Validate(string? name, TelegramOptions options) =>
            TelegramOptions.Describe(options) is { } violation ? ValidateOptionsResult.Fail("Thalos:Channels:Telegram: " + violation) : ValidateOptionsResult.Success;
    }
}
