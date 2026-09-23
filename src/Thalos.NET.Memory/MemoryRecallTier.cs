namespace Thalos.Memory;

/// <summary>Which source answered a recall. Recorded so "the agent knew nothing" is visible rather than inferred.</summary>
public enum MemoryRecallTier
{
    /// <summary>Semantic search over the index.</summary>
    Semantic,

    /// <summary>Index unavailable or empty-handed: the most recent in-scope rows, straight from the store.</summary>
    Recency,

    /// <summary>No rows in scope at all.</summary>
    None,
}
