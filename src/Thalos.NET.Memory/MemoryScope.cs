namespace Thalos.Memory;

/// <summary>
/// What a caller may read: their own owner-wide memories, memories pinned to <paramref name="AgentId"/> (when set), and the
/// host's shared owner's owner-wide memories (when configured). <see cref="Includes"/> is the single visibility rule; indexes
/// whose filters are AND-only query one <see cref="Partitions"/> entry at a time.
/// </summary>
public readonly record struct MemoryScope(string OwnerId, AgentId? AgentId, string? SharedOwnerId = null)
{
    /// <summary>Returns <see langword="true"/> when a record with the given owner/agent is visible in this scope.</summary>
    public bool Includes(string ownerId, AgentId? agentId)
    {
        if (string.Equals(ownerId, OwnerId, StringComparison.Ordinal) && (agentId is null || agentId == AgentId))
        {
            return true;
        }

        return SharedOwnerId is not null && agentId is null && string.Equals(ownerId, SharedOwnerId, StringComparison.Ordinal);
    }

    /// <summary>The (owner, agent) partitions this scope reads: (owner, agent) when an agent is set, (owner, null), and (sharedOwner, null) when configured and different from the owner.</summary>
    public IReadOnlyList<(string OwnerId, AgentId? AgentId)> Partitions()
    {
        var list = new List<(string, AgentId?)>(3);
        if (AgentId is { } agent)
        {
            list.Add((OwnerId, agent));
        }

        list.Add((OwnerId, null));
        if (SharedOwnerId is { } shared && !string.Equals(shared, OwnerId, StringComparison.Ordinal))
        {
            list.Add((shared, null));
        }

        return list;
    }
}
