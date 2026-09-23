using ZeroAlloc.Authorization;

namespace Thalos.Memory;

/// <summary>
/// Resolves the memory owner and pin flag for a caller. Shared by <see cref="MemoryTools"/> (the explicit
/// <c>remember</c>/<c>recall</c>/<c>forget</c>/<c>list</c> tools) and <see cref="MemoryContextProvider"/> (auto-recall,
/// MAF's per-run injection before the first model call) so both read paths and the write path agree on the same owner —
/// two independent copies of this resolution is exactly how a stable-owner caller ends up writing where auto-recall
/// never looks.
/// </summary>
internal static class MemoryOwnerResolver
{
    /// <summary>
    /// The owner id and pin flag for <paramref name="caller"/>, or null when <paramref name="caller"/> is null, blank
    /// or anonymous (there is no owner to read or write for). The owner is <see cref="ISecurityContext.Id"/> unless
    /// <paramref name="caller"/> also implements <see cref="IMemoryOwner"/> and reports a non-blank, non-anonymous
    /// <see cref="IMemoryOwner.MemoryOwnerId"/>, in which case that id is used instead.
    /// </summary>
    public static (string OwnerId, bool PinMemoriesToAgent)? Resolve(ISecurityContext? caller)
    {
        if (caller is null || string.IsNullOrWhiteSpace(caller.Id) || string.Equals(caller.Id, AnonymousSecurityContext.AnonymousId, StringComparison.Ordinal))
        {
            return null;
        }

        var ownerId = caller.Id;
        var pinMemoriesToAgent = false;
        if (caller is IMemoryOwner owner)
        {
            pinMemoriesToAgent = owner.PinMemoriesToAgent;
            if (!string.IsNullOrWhiteSpace(owner.MemoryOwnerId) && !string.Equals(owner.MemoryOwnerId, AnonymousSecurityContext.AnonymousId, StringComparison.Ordinal))
            {
                ownerId = owner.MemoryOwnerId;
            }
        }

        return (ownerId, pinMemoriesToAgent);
    }
}
