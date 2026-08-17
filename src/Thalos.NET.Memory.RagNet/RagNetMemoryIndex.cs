using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Results;

namespace Thalos.Memory.RagNet;

/// <summary>
/// <see cref="IMemoryIndex"/> over a Rag.NET <see cref="IVectorStore"/> (pgvector) and an <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>.
/// One chunk per memory (<c>DocumentId = memory id, ChunkIndex = 0</c>) with metadata <c>thalos=memory, owner_id, agent_id ("" when
/// owner-wide), kind</c>. Rag.NET's metadata filter is AND-only containment, so a search runs one query per
/// <see cref="MemoryScope.Partitions"/> entry — every filter carries <c>owner_id</c>, so a shared <c>rag_chunks</c> table can never
/// leak across owners — and merges by best score. Errors: <see cref="PostgresException"/> → <see cref="AgentErrorCode.MemoryIndexFailed"/>
/// (detail = SQL state), anything else → <see cref="AgentErrorCode.MemoryIndexUnavailable"/> (detail = exception type name).
/// </summary>
public sealed partial class RagNetMemoryIndex(
    IVectorStore vectorStore,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    RagNetMemoryOptions options,
    ILogger<RagNetMemoryIndex>? logger = null) : IMemoryIndex
{
    internal const string MarkerKey = "thalos";
    internal const string MarkerValue = "memory";
    internal const string OwnerKey = "owner_id";
    internal const string AgentKey = "agent_id";
    internal const string KindKey = "kind";

    private readonly ILogger _logger = logger ?? NullLogger<RagNetMemoryIndex>.Instance;

    /// <inheritdoc />
    public async ValueTask<UnitResult<AgentError>> UpsertAsync(IReadOnlyList<MemoryRecord> records, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return UnitResult<AgentError>.Success();
        }

        try
        {
            var vectors = await embeddings.GenerateAsync(records.Select(r => r.Text), null, ct).ConfigureAwait(false);
            if (vectors.Count != records.Count)
            {
                return UnitResult<AgentError>.Failure(AgentError.MemoryIndexFailed("The embedding generator returned a different number of vectors than texts."));
            }

            var chunks = new List<EmbeddedChunk>(records.Count);
            for (var i = 0; i < records.Count; i++)
            {
                var r = records[i];
                chunks.Add(new EmbeddedChunk
                {
                    Chunk = new TextChunk { Text = r.Text, DocumentId = new DocumentId(r.Id.ToString()), ChunkIndex = 0, Metadata = Metadata(r.OwnerId, r.AgentId, r.Kind) },
                    Embedding = vectors[i].Vector,
                });
            }

            await vectorStore.StoreAsync(chunks, ct).ConfigureAwait(false);
            return UnitResult<AgentError>.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return UnitResult<AgentError>.Failure(Map(ex, "upsert"));
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<MemoryHit>, AgentError>> SearchAsync(string query, MemoryScope scope, MemorySearchOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrEmpty(scope.OwnerId))
        {
            return Result<IReadOnlyList<MemoryHit>, AgentError>.Success([]);
        }

        try
        {
            var vector = await embeddings.GenerateVectorAsync(query, null, ct).ConfigureAwait(false);
            var topK = Math.Max(1, options.TopK);
            var best = new Dictionary<MemoryId, double>();
            foreach (var (owner, agent) in scope.Partitions())
            {
                var results = await vectorStore.SearchAsync(vector, new SearchOptions { TopK = topK, MinScore = options.MinScore, MetadataFilter = Metadata(owner, agent, kind: null) }, ct).ConfigureAwait(false);
                foreach (var result in results)
                {
                    if (!MemoryId.TryParse(result.Chunk.DocumentId.Value, null, out var id))
                    {
                        continue; // not one of ours
                    }

                    if (!best.TryGetValue(id, out var score) || result.Score > score)
                    {
                        best[id] = result.Score;
                    }
                }
            }

            IReadOnlyList<MemoryHit> hits = best.OrderByDescending(kv => kv.Value).Take(topK).Select(kv => new MemoryHit(kv.Key, kv.Value)).ToList();
            return Result<IReadOnlyList<MemoryHit>, AgentError>.Success(hits);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Result<IReadOnlyList<MemoryHit>, AgentError>.Failure(Map(ex, "search"));
        }
    }

    /// <inheritdoc />
    public async ValueTask<UnitResult<AgentError>> RemoveAsync(MemoryId id, CancellationToken ct)
    {
        try
        {
            await vectorStore.DeleteByDocumentIdAsync(id.ToString(), ct).ConfigureAwait(false);
            return UnitResult<AgentError>.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return UnitResult<AgentError>.Failure(Map(ex, "remove"));
        }
    }

    /// <inheritdoc />
    /// <remarks>Embeds a probe text (checks the generator, learns the dimensions), compares with <see cref="RagNetMemoryOptions.VectorDimensions"/>, then runs a filtered search (checks the table). Never throws.</remarks>
    public async ValueTask<Result<MemoryIndexHealth, AgentError>> ProbeAsync(CancellationToken ct)
    {
        try
        {
            var vector = await embeddings.GenerateVectorAsync("thalos memory probe", null, ct).ConfigureAwait(false);
            var dims = vector.Length;
            if (dims != options.VectorDimensions)
            {
                return Result<MemoryIndexHealth, AgentError>.Success(new MemoryIndexHealth(false, dims,
                    string.Create(CultureInfo.InvariantCulture, $"the embedding generator produces {dims}-dimensional vectors but VectorDimensions is {options.VectorDimensions}")));
            }

            await vectorStore.SearchAsync(vector, new SearchOptions { TopK = 1, MinScore = 0, MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { [MarkerKey] = MarkerValue } }, ct).ConfigureAwait(false);
            return Result<MemoryIndexHealth, AgentError>.Success(new MemoryIndexHealth(true, dims));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var error = Map(ex, "probe");
            return Result<MemoryIndexHealth, AgentError>.Success(new MemoryIndexHealth(false, null, error.Detail ?? error.Message));
        }
    }

    private static Dictionary<string, MetadataValue> Metadata(string owner, AgentId? agent, MemoryKind? kind)
    {
        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            [MarkerKey] = MarkerValue,
            [OwnerKey] = owner,
            [AgentKey] = agent?.ToString() ?? "",
        };
        if (kind is not null)
        {
            metadata[KindKey] = kind.Value;
        }

        return metadata;
    }

    internal AgentError Map(Exception ex, string operation)
    {
        LogFailed(_logger, operation, ex.GetType().Name, ex.Message, ex); // raw message to the log only
        return ex is PostgresException pg
            ? AgentError.MemoryIndexFailed($"The memory index rejected the {operation}.", pg.SqlState)
            : AgentError.MemoryIndexUnavailable($"The memory index is unavailable ({operation}).", ex.GetType().Name);
    }

    [LoggerMessage(EventId = 520, Level = LogLevel.Warning, Message = "Rag.NET memory index {Operation} failed with {ExceptionType}: {Error}")]
    private static partial void LogFailed(ILogger logger, string operation, string exceptionType, string error, Exception exception);
}
