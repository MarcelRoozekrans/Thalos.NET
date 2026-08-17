using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rag.NET.PgVector;

namespace Thalos.Memory.RagNet;

/// <summary>Service key under which the adapter's <see cref="PgVectorStore"/> is registered (so it never collides with a host's own Rag.NET store).</summary>
public static class RagNetMemory
{
    public const string VectorStoreKey = "thalos-memory";
}

/// <summary>Registers the Rag.NET pgvector adapter as the memory index. Call <c>UseMemory(...)</c> too (in any order).</summary>
public static partial class RagNetMemoryThalosBuilderExtensions
{
    /// <summary>
    /// Uses <c>PgVectorStore(connectionString, vectorDimensions)</c> + the registered <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c>
    /// as the <see cref="IMemoryIndex"/>. When <see cref="RagNetMemoryOptions.EnsureSchemaOnStartup"/> is set, a hosted service runs
    /// <c>InitializeAsync()</c> at startup and fails fast on a dimension mismatch. Table: Rag.NET's hard-coded <c>rag_chunks</c>.
    /// </summary>
    /// <remarks>
    /// A host without an <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c> still starts: the index resolves to
    /// <see cref="UnavailableMemoryIndex.Instance"/> (remember stores with <c>IndexPending</c>, recall adds nothing) with a one-time warning,
    /// and the schema initializer skips the generator dimension check but still creates the table at the configured dimensions so a later
    /// <c>IMemoryService.ReindexAsync</c> (once a generator is registered) can fill it.
    /// </remarks>
    public static ThalosBuilder UseRagNetMemory(this ThalosBuilder builder, Action<RagNetMemoryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new RagNetMemoryOptions();
        configure(options);
        options.Validate(nameof(configure));

        var services = builder.Services;
        services.AddSingleton(options);
        services.TryAddKeyedSingleton(RagNetMemory.VectorStoreKey, (_, _) => new PgVectorStore(options.ConnectionString, options.VectorDimensions));
        services.Replace(ServiceDescriptor.Singleton<IMemoryIndex>(sp =>
        {
            var embeddings = sp.GetService<IEmbeddingGenerator<string, Embedding<float>>>();
            if (embeddings is null)
            {
                // singleton factory: runs once per container, so this warning is emitted once
                LogNoEmbeddingGenerator(sp.GetService<ILogger<RagNetMemoryIndex>>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RagNetMemoryIndex>.Instance);
                return UnavailableMemoryIndex.Instance;
            }

            return new RagNetMemoryIndex(
                sp.GetRequiredKeyedService<PgVectorStore>(RagNetMemory.VectorStoreKey),
                embeddings,
                options,
                sp.GetService<ILogger<RagNetMemoryIndex>>());
        }));
        if (options.EnsureSchemaOnStartup)
        {
            services.AddSingleton<IHostedService>(sp => new RagNetMemorySchemaInitializer(
                sp.GetRequiredKeyedService<PgVectorStore>(RagNetMemory.VectorStoreKey),
                options,
                sp.GetService<IEmbeddingGenerator<string, Embedding<float>>>(),
                sp.GetService<ILogger<RagNetMemorySchemaInitializer>>()));
        }

        return builder;
    }

    /// <summary>Shorthand for the two required settings.</summary>
    public static ThalosBuilder UseRagNetMemory(this ThalosBuilder builder, string connectionString, int vectorDimensions) =>
        builder.UseRagNetMemory(o => { o.ConnectionString = connectionString; o.VectorDimensions = vectorDimensions; });

    [LoggerMessage(EventId = 531, Level = LogLevel.Warning, Message = "Thalos.NET.Memory.RagNet: no IEmbeddingGenerator<string, Embedding<float>> is registered — the memory index is unavailable (remember stores with IndexPending, recall adds nothing) until one is registered and IMemoryService.ReindexAsync runs")]
    private static partial void LogNoEmbeddingGenerator(ILogger logger);
}
