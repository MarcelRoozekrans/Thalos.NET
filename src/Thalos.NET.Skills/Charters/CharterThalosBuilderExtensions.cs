using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Thalos.Skills.Charters;

/// <summary>Registers chartered agents (Thalos.NET.Skills.Charters) on a <see cref="ThalosBuilder"/>.</summary>
public static class CharterThalosBuilderExtensions
{
    /// <summary>
    /// Enables chartered agents: the start-up file sync, an in-memory <see cref="IRoleCharterStore"/> (replace with
    /// <see cref="UseRoleCharterStore{TStore}"/>) and a <see cref="CharteredAgentCatalog"/> that replaces whatever
    /// <see cref="IAgentCatalog"/> would otherwise be registered — including the default <c>OptionsAgentCatalog</c>
    /// that <c>AddThalos</c> registers afterwards with <c>TryAddSingleton</c>, which is therefore a no-op. Idempotent
    /// (registrations are <c>TryAdd</c>; every <paramref name="configure"/> runs, last wins).
    /// </summary>
    /// <remarks>
    /// <see cref="CharterOptions.Envelopes"/> carries a typed <see cref="AgentId"/>, which is not configuration-bindable
    /// (see <c>ThalosOptions.SectionName</c>), so envelopes are only ever added here, in code. An envelope whose
    /// <see cref="AgentEnvelope.Name"/> collides with a <c>ThalosOptions.Agents</c> entry fails options validation at
    /// host start (<c>ValidateOnStart</c>), not at registration time — options aren't resolved yet.
    /// </remarks>
    public static ThalosBuilder UseRoleCharters(this ThalosBuilder builder, Action<CharterOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = builder.Services.AddOptions<CharterOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        return Register(builder, options);
    }

    /// <summary>
    /// Same as <see cref="UseRoleCharters(ThalosBuilder, Action{CharterOptions}?)"/>, binding only
    /// <see cref="CharterOptions.Roots"/> from the <c>Thalos:Charters</c> section of <paramref name="configuration"/>.
    /// <see cref="CharterOptions.Envelopes"/> is get-only and never touched by this overload — use the
    /// <see cref="Action{CharterOptions}"/> overload (or call both; later registrations layer configuration) to add
    /// envelopes in code.
    /// </summary>
    public static ThalosBuilder UseRoleCharters(this ThalosBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = builder.Services.AddOptions<CharterOptions>().Bind(configuration.GetSection(CharterOptions.SectionName));
        return Register(builder, options);
    }

    /// <summary>
    /// Uses <typeparamref name="TStore"/> as the role charter store, replacing the default in-memory one. Singleton —
    /// take <see cref="IServiceScopeFactory"/> for scoped resources. May be called before or after
    /// <see cref="UseRoleCharters(ThalosBuilder, Action{CharterOptions}?)"/>: this replaces, <c>UseRoleCharters</c> only
    /// tries to add, so <typeparamref name="TStore"/> wins either way.
    /// </summary>
    public static ThalosBuilder UseRoleCharterStore<TStore>(this ThalosBuilder builder) where TStore : class, IRoleCharterStore
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Replace(ServiceDescriptor.Singleton<TStore, TStore>());
        builder.Services.Replace(ServiceDescriptor.Singleton<IRoleCharterStore>(sp => sp.GetRequiredService<TStore>()));
        return builder;
    }

    private static ThalosBuilder Register(ThalosBuilder builder, OptionsBuilder<CharterOptions> options)
    {
        options.ValidateOnStart();

        var services = builder.Services;
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CharterOptions>, CharterOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<InMemoryRoleCharterStore>();
        services.TryAddSingleton<IRoleCharterStore>(sp => sp.GetRequiredService<InMemoryRoleCharterStore>());
        services.TryAddSingleton<CharteredAgentCatalog>();
        // Replace (not TryAdd): a chartered catalog must win over the default OptionsAgentCatalog that AddThalos
        // registers with TryAddSingleton after this builder callback returns.
        services.Replace(ServiceDescriptor.Singleton<IAgentCatalog>(sp => sp.GetRequiredService<CharteredAgentCatalog>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, CharterSyncService>());
        return builder;
    }

    /// <summary>The first violation as text, or null when the options are valid.</summary>
    internal static string? Describe(CharterOptions options, ThalosOptions thalos)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(thalos);

        var configNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var agent in thalos.Agents)
        {
            configNames.Add(agent.Name);
        }

        foreach (var envelope in options.Envelopes)
        {
            if (configNames.Contains(envelope.Name))
            {
                return $"Envelopes: '{envelope.Name}' collides with a Thalos:Agents entry of the same name; a chartered role and a config agent may not share a name.";
            }
        }

        return null;
    }

    /// <summary>Runs <see cref="Describe"/> at host start via <c>ValidateOnStart</c>.</summary>
    private sealed class CharterOptionsValidator(IOptions<ThalosOptions> thalos) : IValidateOptions<CharterOptions>
    {
        public ValidateOptionsResult Validate(string? name, CharterOptions options) =>
            Describe(options, thalos.Value) is { } violation ? ValidateOptionsResult.Fail("Thalos:Charters: " + violation) : ValidateOptionsResult.Success;
    }
}
