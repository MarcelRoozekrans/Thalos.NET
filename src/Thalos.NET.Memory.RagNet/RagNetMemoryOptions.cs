namespace Thalos.Memory.RagNet;

/// <summary>Configuration for <c>UseRagNetMemory</c>. Rag.NET's <c>PgVectorStore</c> builds its own Npgsql pool from <see cref="ConnectionString"/> and uses the hard-coded table <c>rag_chunks</c> (shared with any other Rag.NET use on that database).</summary>
/// <remarks>
/// Sharp edge: Rag.NET searches through pgvector's HNSW index (approximate) with <c>hnsw.iterative_scan = relaxed_order</c>; the metadata
/// (owner/agent) filter is applied to the index's candidates and pgvector stops at <c>hnsw.max_scan_tuples</c> (20 000 by default), so on
/// a large <c>rag_chunks</c> shared with many owners or other Rag.NET documents a search may return fewer than TopK hits even though matching
/// rows exist. Give memory its own database (or raise <c>hnsw.max_scan_tuples</c>) when the table grows large.
/// </remarks>
public sealed class RagNetMemoryOptions
{
    /// <summary>Npgsql connection string of the pgvector-enabled PostgreSQL database (the adapter creates the <c>vector</c> extension and the <c>rag_chunks</c> table when <see cref="EnsureSchemaOnStartup"/> is set). Required.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Must equal the embedding generator's output size (e.g. 768 for nomic-embed-text). Checked at startup and by <c>ProbeAsync</c>.</summary>
    public int VectorDimensions { get; set; }

    /// <summary>Run <c>PgVectorStore.InitializeAsync()</c> from a hosted service at startup (creates extension, table, indexes; fails fast on a dimension mismatch).</summary>
    public bool EnsureSchemaOnStartup { get; set; } = true;

    /// <summary>Throws <see cref="ArgumentException"/> (message names the offending member; <paramref name="paramName"/> is the caller's parameter that produced these options).</summary>
    internal void Validate(string paramName)
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new ArgumentException("RagNetMemoryOptions.ConnectionString is required.", paramName);
        }

        if (VectorDimensions <= 0)
        {
            throw new ArgumentException("RagNetMemoryOptions.VectorDimensions must be positive.", paramName);
        }
    }
}
