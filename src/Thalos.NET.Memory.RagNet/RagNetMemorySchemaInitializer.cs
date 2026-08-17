using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.PgVector;

namespace Thalos.Memory.RagNet;

/// <summary>
/// Runs <c>PgVectorStore.InitializeAsync()</c> once at startup; fails fast (throws) when the generator's known dimensions or the existing
/// table disagree with <see cref="RagNetMemoryOptions.VectorDimensions"/>. Without a generator (<see langword="null"/>) the generator check
/// is skipped and the table is still created at the configured dimensions (so a later reindex can fill it).
/// </summary>
internal sealed partial class RagNetMemorySchemaInitializer(
    PgVectorStore store,
    RagNetMemoryOptions options,
    IEmbeddingGenerator<string, Embedding<float>>? embeddings,
    ILogger<RagNetMemorySchemaInitializer>? logger = null) : IHostedService
{
    private readonly ILogger _logger = logger ?? NullLogger<RagNetMemorySchemaInitializer>.Instance;

    public async Task StartAsync(CancellationToken cancellationToken)
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
            throw new InvalidOperationException(
                "Thalos.NET.Memory.RagNet: rag_chunks holds vectors of a different dimension than configured — change VectorDimensions to match, or drop the table and run IMemoryService.ReindexAsync(new ReindexOptions { PendingOnly = false }). " + ex.Message, ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 530, Level = LogLevel.Information, Message = "Rag.NET memory index schema ready (rag_chunks, vector({Dimensions}))")]
    private static partial void LogInitialized(ILogger logger, int dimensions);

    [LoggerMessage(EventId = 532, Level = LogLevel.Information, Message = "Rag.NET memory index: no IEmbeddingGenerator<string, Embedding<float>> is registered; skipping the generator dimension check and creating rag_chunks at vector({Dimensions})")]
    private static partial void LogNoGenerator(ILogger logger, int dimensions);
}
