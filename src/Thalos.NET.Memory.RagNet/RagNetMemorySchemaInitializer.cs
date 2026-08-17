using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.PgVector;

namespace Thalos.Memory.RagNet;

/// <summary>
/// Runs <c>PgVectorStore.InitializeAsync()</c> once at startup — in <see cref="StartingAsync"/>, i.e. before any <see cref="IHostedService.StartAsync"/>
/// runs, so a host's own hosted services (e.g. a reindex job) find the schema in place. Fails fast (throws) when the generator's known dimensions
/// or the existing table disagree with <see cref="RagNetMemoryOptions.VectorDimensions"/>. Without a generator (<see langword="null"/>) the generator
/// check is skipped and the table is still created at the configured dimensions (so a later reindex can fill it).
/// </summary>
internal sealed partial class RagNetMemorySchemaInitializer(
    [FromKeyedServices(RagNetMemory.VectorStoreKey)] PgVectorStore store,
    RagNetMemoryOptions options,
    IEmbeddingGenerator<string, Embedding<float>>? embeddings = null,
    ILogger<RagNetMemorySchemaInitializer>? logger = null) : IHostedLifecycleService
{
    private const string Prefix = "Thalos.NET.Memory.RagNet: could not initialise rag_chunks";
    private const string DimensionAdvice = " — the message reports a vector(N) mismatch: set VectorDimensions to match the table, or drop the table and run IMemoryService.ReindexAsync(new ReindexOptions { PendingOnly = false })";
    private const string OtherAdvice = " — other causes: duplicate (document_id, chunk_index) rows or a conflicting index name";

    private readonly ILogger _logger = logger ?? NullLogger<RagNetMemorySchemaInitializer>.Instance;

    /// <summary>Checks the generator's reported dimensions against the options, then creates/verifies the schema. Throws to fail the host start.</summary>
    /// <exception cref="InvalidOperationException">Generator dimensions or the existing <c>rag_chunks</c> table disagree with <see cref="RagNetMemoryOptions.VectorDimensions"/>, or Rag.NET could not initialise the table.</exception>
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (embeddings is null)
        {
            LogNoGenerator(_logger, options.VectorDimensions);
        }
        else if (embeddings.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelDimensions is { } dims && dims != options.VectorDimensions)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Thalos.NET.Memory.RagNet: VectorDimensions is {options.VectorDimensions} but the embedding generator reports {dims} dimensions. Set VectorDimensions = {dims}; if rag_chunks already holds vectors of another size, drop the table and run IMemoryService.ReindexAsync(new ReindexOptions {{ PendingOnly = false }})."));
        }

        try
        {
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            LogInitialized(_logger, options.VectorDimensions);
        }
        catch (InvalidOperationException ex)
        {
            var advice = ex.Message.Contains("vector(", StringComparison.Ordinal) ? DimensionAdvice : OtherAdvice;
            throw new InvalidOperationException(Prefix + advice + ". " + ex.Message, ex);
        }
    }

    /// <summary>No-op — the work happens in <see cref="StartingAsync"/>.</summary>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>No-op.</summary>
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>No-op.</summary>
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>No-op.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>No-op.</summary>
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 541, Level = LogLevel.Information, Message = "Rag.NET memory index schema ready (rag_chunks, vector({Dimensions}))")]
    private static partial void LogInitialized(ILogger logger, int dimensions);

    [LoggerMessage(EventId = 543, Level = LogLevel.Information, Message = "Rag.NET memory index: no IEmbeddingGenerator<string, Embedding<float>> is registered; skipping the generator dimension check and creating rag_chunks at vector({Dimensions})")]
    private static partial void LogNoGenerator(ILogger logger, int dimensions);
}
