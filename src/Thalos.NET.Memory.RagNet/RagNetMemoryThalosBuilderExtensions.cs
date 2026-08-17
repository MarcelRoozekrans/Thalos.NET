using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.PgVector;

namespace Thalos.Memory.RagNet;

/// <summary>Constants of the Rag.NET memory adapter.</summary>
public static class RagNetMemory
{
    /// <summary>Service key under which the adapter's <see cref="PgVectorStore"/> is registered (keyed, so it never collides with a host's own unkeyed Rag.NET store). Resolve with <c>GetRequiredKeyedService&lt;PgVectorStore&gt;(RagNetMemory.VectorStoreKey)</c>.</summary>
    public const string VectorStoreKey = "thalos-memory";
}

/// <summary>Registers the Rag.NET pgvector adapter as the memory index. Call <c>UseMemory(...)</c> too (in any order).</summary>
public static partial class RagNetMemoryThalosBuilderExtensions
{
    /// <summary>
    /// Uses <c>PgVectorStore(connectionString, vectorDimensions)</c> + the registered <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c>
    /// as the <see cref="IMemoryIndex"/>. When <see cref="RagNetMemoryOptions.EnsureSchemaOnStartup"/> is set, a hosted service runs
    /// <c>InitializeAsync()</c> at startup (in <see cref="IHostedLifecycleService.StartingAsync"/>, before other hosted services start)
    /// and fails fast on a dimension mismatch. Table: Rag.NET's hard-coded <c>rag_chunks</c>.
    /// </summary>
    /// <remarks>
    /// <para>Last call wins: options, the keyed <see cref="PgVectorStore"/>, the index and the (single) schema initializer all reflect the
    /// most recent <c>UseRagNetMemory</c> call.</para>
    /// <para>A host without an <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c> still starts: the index resolves to
    /// <see cref="UnavailableMemoryIndex.Instance"/> (remember stores with <c>IndexPending</c>, recall adds nothing) with a one-time warning,
    /// and the schema initializer skips the generator dimension check but still creates the table at the configured dimensions so a later
    /// <c>IMemoryService.ReindexAsync</c> (once a generator is registered) can fill it.</para>
    /// </remarks>
    public static ThalosBuilder UseRagNetMemory(this ThalosBuilder builder, Action<RagNetMemoryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new RagNetMemoryOptions();
        configure(options);
        options.Validate(nameof(configure));

        var services = builder.Services;
        services.Replace(ServiceDescriptor.Singleton(options));
        services.Replace(ServiceDescriptor.KeyedSingleton<PgVectorStore>(RagNetMemory.VectorStoreKey, static (sp, _) =>
        {
            var o = sp.GetRequiredService<RagNetMemoryOptions>();
            return new PgVectorStore(o.ConnectionString, o.VectorDimensions);
        }));
        services.Replace(ServiceDescriptor.Singleton<IMemoryIndex>(static sp =>
        {
            var embeddings = sp.GetService<IEmbeddingGenerator<string, Embedding<float>>>();
            if (embeddings is null)
            {
                // singleton factory: runs once per container, so this warning is emitted once
                LogNoEmbeddingGenerator(sp.GetService<ILogger<RagNetMemoryIndex>>() ?? NullLogger<RagNetMemoryIndex>.Instance);
                return UnavailableMemoryIndex.Instance;
            }

            return new RagNetMemoryIndex(
                sp.GetRequiredKeyedService<PgVectorStore>(RagNetMemory.VectorStoreKey),
                embeddings,
                sp.GetRequiredService<RagNetMemoryOptions>(),
                sp.GetService<ILogger<RagNetMemoryIndex>>());
        }));

        // exactly one initializer, reflecting the last call (a later call with EnsureSchemaOnStartup = false removes it)
        for (var i = services.Count - 1; i >= 0; i--)
        {
            var d = services[i];
            if (d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(RagNetMemorySchemaInitializer))
            {
                services.RemoveAt(i);
            }
        }

        if (options.EnsureSchemaOnStartup)
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RagNetMemorySchemaInitializer>());
        }

        return builder;
    }

    /// <summary>Shorthand for the two required settings.</summary>
    public static ThalosBuilder UseRagNetMemory(this ThalosBuilder builder, string connectionString, int vectorDimensions) =>
        builder.UseRagNetMemory(o => { o.ConnectionString = connectionString; o.VectorDimensions = vectorDimensions; });

    [LoggerMessage(EventId = 542, Level = LogLevel.Warning, Message = "Thalos.NET.Memory.RagNet: no IEmbeddingGenerator<string, Embedding<float>> is registered — the memory index is unavailable (remember stores with IndexPending, recall adds nothing) until one is registered and IMemoryService.ReindexAsync runs")]
    private static partial void LogNoEmbeddingGenerator(ILogger logger);
}
