namespace Thalos.Memory.RagNet;

/// <summary>Configuration for <c>UseRagNetMemory</c>. Rag.NET's <c>PgVectorStore</c> builds its own Npgsql pool from <see cref="ConnectionString"/> and uses the hard-coded table <c>rag_chunks</c> (shared with any other Rag.NET use on that database).</summary>
public sealed class RagNetMemoryOptions
{
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
