using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Thalos.Runtime;

namespace Thalos.Skills;

/// <summary>Registers Thalos.NET.Skills on a <see cref="ThalosBuilder"/>.</summary>
public static partial class SkillThalosBuilderExtensions
{
    /// <summary>
    /// Enables skills: the start-up file sync, an in-memory <see cref="ISkillStore"/> (replace with <see cref="UseSkillStore{TStore}"/>),
    /// an <see cref="ISkillIndex"/> — <see cref="InMemorySkillIndex"/> over the registered
    /// <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c>, or <see cref="UnavailableSkillIndex"/> when none is
    /// registered (replace with <see cref="UseSkillIndex{TIndex}"/>) — the catalogue context provider and the <c>skills</c> tool
    /// source. Idempotent (registrations are TryAdd; every <paramref name="configure"/> runs, last wins).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="SkillOptions"/> are normalised and validated when first resolved and at host start (<c>ValidateOnStart</c>):
    /// roots are trimmed, de-duplicated and made absolute; <see cref="SkillCatalogueOptions.MaxChars"/> must be ≥ 0,
    /// <see cref="SkillSearchOptions.TopK"/> ≥ 1 and <see cref="SkillSearchOptions.MinScore"/> in [0, 1]; otherwise an
    /// <see cref="OptionsValidationException"/> is thrown. A root that does not exist is deliberately <em>not</em> a
    /// configuration error — the sync logs it and leaves the library alone, deactivating nothing that run whether one root
    /// failed or all of them did, because a path typo must never retire a skill.
    /// </para>
    /// <para>
    /// <see cref="SkillOptions.Enabled"/> is a runtime switch, not a registration one: the services are always registered and
    /// each one turns itself off, so a host can bind the flag from configuration that is not read until the provider is built.
    /// </para>
    /// </remarks>
    [RequiresUnreferencedCode("Discovers tool methods via reflection.")]
    [RequiresDynamicCode("Tool parameters and results are serialized via reflection-based JSON.")]
    public static ThalosBuilder UseSkills(this ThalosBuilder builder, Action<SkillOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = builder.Services.AddOptions<SkillOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        return Register(builder, options);
    }

    /// <summary>Same as <see cref="UseSkills(ThalosBuilder, Action{SkillOptions}?)"/>, options bound from the <c>Thalos:Skills</c> section of <paramref name="configuration"/>.</summary>
    [RequiresUnreferencedCode("Discovers tool methods via reflection.")]
    [RequiresDynamicCode("Tool parameters and results are serialized via reflection-based JSON.")]
    public static ThalosBuilder UseSkills(this ThalosBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = builder.Services.AddOptions<SkillOptions>().Bind(configuration.GetSection(SkillOptions.SectionName));
        return Register(builder, options);
    }

    /// <summary>
    /// Uses <typeparamref name="TStore"/> as the skill store, wrapped in the telemetry proxy. Singleton — take
    /// <see cref="IServiceScopeFactory"/> for scoped resources. May be called before or after <see cref="UseSkills(ThalosBuilder, Action{SkillOptions}?)"/>:
    /// this replaces, <c>UseSkills</c> only tries to add, so <typeparamref name="TStore"/> wins either way.
    /// </summary>
    public static ThalosBuilder UseSkillStore<TStore>(this ThalosBuilder builder) where TStore : class, ISkillStore
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Replace(ServiceDescriptor.Singleton<TStore, TStore>());
        builder.Services.Replace(ServiceDescriptor.Singleton<ISkillStore>(sp => new SkillStoreInstrumented(sp.GetRequiredService<TStore>())));
        return builder;
    }

    /// <summary>Uses <typeparamref name="TIndex"/> as the skill index (replacing the default), before or after <c>UseSkills</c>.</summary>
    public static ThalosBuilder UseSkillIndex<TIndex>(this ThalosBuilder builder) where TIndex : class, ISkillIndex
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Replace(ServiceDescriptor.Singleton<ISkillIndex, TIndex>());
        return builder;
    }

    [RequiresUnreferencedCode("Discovers tool methods via reflection.")]
    [RequiresDynamicCode("Tool parameters and results are serialized via reflection-based JSON.")]
    private static ThalosBuilder Register(ThalosBuilder builder, OptionsBuilder<SkillOptions> options)
    {
        options.PostConfigure(Normalize).ValidateOnStart();

        var services = builder.Services;
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SkillOptions>, SkillOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.AddThalosSkillsServices(); // generated by ZeroAlloc.Inject: SkillCatalogue by concrete type (TryAdd)
        services.TryAddSingleton<InMemorySkillStore>();
        services.TryAddSingleton<ISkillStore>(sp => new SkillStoreInstrumented(sp.GetRequiredService<InMemorySkillStore>()));
        services.TryAddSingleton<ISkillIndex>(sp =>
        {
            if (sp.GetService<IEmbeddingGenerator<string, Embedding<float>>>() is { } embeddings)
            {
                return new InMemorySkillIndex(embeddings);
            }

            // hoisted to a local: CA1873 rejects building the logger inside the LoggerMessage call argument
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger(typeof(SkillThalosBuilderExtensions)) ?? NullLogger.Instance;
            LogNoGenerator(logger);
            return UnavailableSkillIndex.Instance;
        });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentContextProviderSource, SkillContextProviderSource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IToolSource, SkillToolSource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SkillSyncService>());
        return builder;
    }

    /// <summary>Trims roots, drops blanks, makes them absolute and removes duplicates (the sync searches them in order).</summary>
    /// <remarks>
    /// A root that no path can express — a NUL is legal JSON and reaches here straight from a configuration file — is kept
    /// verbatim rather than expanded, so <see cref="Describe"/> reports it as a <c>Thalos:Skills</c> validation failure like
    /// every other misconfiguration instead of an <see cref="ArgumentException"/> escaping <c>PostConfigure</c>.
    /// </remarks>
    private static void Normalize(SkillOptions o)
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var roots = new List<string>(o.Roots.Count);
        for (var i = 0; i < o.Roots.Count; i++)
        {
            var root = o.Roots[i]?.Trim();
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            if (FullPath(root) is not { } full)
            {
                roots.Add(root);
                continue;
            }

            if (seen.Add(full))
            {
                roots.Add(full);
            }
        }

        o.Roots = roots;
    }

    /// <summary>The absolute, separator-trimmed form of <paramref name="root"/>, or null when no path can hold it.</summary>
    private static string? FullPath(string root)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    /// <summary>The first violation as text, or null when the options are valid.</summary>
    internal static string? Describe(SkillOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (o.Catalogue is null || o.Search is null || o.Roots is null)
        {
            return "Roots, Catalogue and Search must not be null.";
        }

        for (var i = 0; i < o.Roots.Count; i++)
        {
            // The value is never echoed: what makes it invalid is a character that cannot be printed in the first place.
            if (o.Roots[i] is { } root && root.Trim().Length > 0 && FullPath(root) is null)
            {
                return string.Create(CultureInfo.InvariantCulture, $"Roots[{i}] is not a usable file-system path (it holds a NUL or another character no path can express).");
            }
        }

        if (o.Catalogue.MaxChars < 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Catalogue.MaxChars must be >= 0 (0 means no budget; was {o.Catalogue.MaxChars}).");
        }

        if (o.Search.TopK < 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Search.TopK must be >= 1 (was {o.Search.TopK}).");
        }

        if (double.IsNaN(o.Search.MinScore) || o.Search.MinScore is < 0 or > 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Search.MinScore must be in [0, 1] (was {o.Search.MinScore}).");
        }

        return null;
    }

    [LoggerMessage(EventId = 564, Level = LogLevel.Information, Message = "No IEmbeddingGenerator<string, Embedding<float>> is registered; skills__search is unavailable and the <skills> catalogue is the only way in")]
    private static partial void LogNoGenerator(ILogger logger);

    /// <summary>Runs <see cref="Describe"/> when the options are first resolved, and at host start via <c>ValidateOnStart</c>.</summary>
    private sealed class SkillOptionsValidator : IValidateOptions<SkillOptions>
    {
        public ValidateOptionsResult Validate(string? name, SkillOptions options) =>
            Describe(options) is { } violation ? ValidateOptionsResult.Fail("Thalos:Skills: " + violation) : ValidateOptionsResult.Success;
    }
}
