using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Thalos.Channels.Console;
using ZeroAlloc.Authorization;

namespace Thalos.Channels;

/// <summary>Registers Thalos.NET.Channels on a <see cref="ThalosBuilder"/>.</summary>
public static class ChannelThalosBuilderExtensions
{
    /// <summary>
    /// Enables channels: <see cref="ChannelOptions"/>, an in-memory <see cref="IConversationMap"/> (replace with
    /// <see cref="UseConversationMap{TMap}"/>) and the <see cref="ChannelPump"/> hosted service that reads every
    /// registered <see cref="IChannelSource"/> and delivers through the matching <see cref="IChannelAdapter"/> — add
    /// a channel with <see cref="AddConsoleChannel"/> or a package such as Telegram's registration. Idempotent
    /// (registrations are TryAdd; every <paramref name="configure"/> runs, last wins).
    /// </summary>
    /// <remarks>
    /// <see cref="ChannelOptions"/> are validated when first resolved and at host start (<c>ValidateOnStart</c>) via
    /// <see cref="ChannelOptions.Describe"/>; a violation throws <see cref="OptionsValidationException"/>.
    /// </remarks>
    public static ThalosBuilder UseChannels(this ThalosBuilder builder, Action<ChannelOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = builder.Services.AddOptions<ChannelOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        return Register(builder, options);
    }

    /// <summary>Same as <see cref="UseChannels(ThalosBuilder, Action{ChannelOptions}?)"/>, options bound from the <c>Thalos:Channels</c> section of <paramref name="configuration"/>.</summary>
    public static ThalosBuilder UseChannels(this ThalosBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = builder.Services.AddOptions<ChannelOptions>().Bind(configuration.GetSection(ChannelOptions.SectionName));
        return Register(builder, options);
    }

    /// <summary>
    /// Uses <typeparamref name="TMap"/> as the conversation map, replacing the default <see cref="InMemoryConversationMap"/>.
    /// Singleton — take <see cref="IServiceScopeFactory"/> for scoped resources. May be called before or after
    /// <see cref="UseChannels(ThalosBuilder, Action{ChannelOptions}?)"/>: this replaces, <c>UseChannels</c> only
    /// tries to add, so <typeparamref name="TMap"/> wins either way.
    /// </summary>
    public static ThalosBuilder UseConversationMap<TMap>(this ThalosBuilder builder) where TMap : class, IConversationMap
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Replace(ServiceDescriptor.Singleton<IConversationMap, TMap>());
        return builder;
    }

    /// <summary>
    /// Registers the in-box console channel: an <see cref="IChannelSource"/> reading real standard input and an
    /// <see cref="IChannelAdapter"/> writing real standard output, both added via <c>TryAddEnumerable</c> so calling
    /// this twice does not double-pump the console. The console's caller is <see cref="AnonymousSecurityContext.Instance"/>
    /// — a local terminal has no principal of its own to carry.
    /// </summary>
    public static ThalosBuilder AddConsoleChannel(this ThalosBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // "Console" is ambiguous here: Thalos.Channels.Console (the namespace two lines up) shadows the
        // System.Console TYPE for any unqualified reference reachable from this file, so the real console streams
        // must be reached through the fully qualified global:: alias.
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelSource>(
            new ConsoleChannelSource(global::System.Console.In, AnonymousSecurityContext.Instance)));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelAdapter>(
            new ConsoleChannelAdapter(global::System.Console.Out)));

        return builder;
    }

    private static ThalosBuilder Register(ThalosBuilder builder, OptionsBuilder<ChannelOptions> options)
    {
        options.ValidateOnStart();

        var services = builder.Services;
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ChannelOptions>, ChannelOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IConversationMap, InMemoryConversationMap>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ChannelPump>());
        return builder;
    }

    /// <summary>Runs <see cref="ChannelOptions.Describe"/> when the options are first resolved, and at host start via <c>ValidateOnStart</c>.</summary>
    private sealed class ChannelOptionsValidator : IValidateOptions<ChannelOptions>
    {
        public ValidateOptionsResult Validate(string? name, ChannelOptions options) =>
            ChannelOptions.Describe(options) is { } violation ? ValidateOptionsResult.Fail("Thalos:Channels: " + violation) : ValidateOptionsResult.Success;
    }
}
