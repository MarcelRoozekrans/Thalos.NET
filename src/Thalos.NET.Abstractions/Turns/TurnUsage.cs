namespace Thalos;

/// <summary>
/// Token usage for one turn (summed over all model round-trips inside the turn).
/// Seed an accumulation with <see cref="Empty(string)"/> and fold with <c>+</c>.
/// </summary>
public readonly record struct TurnUsage(int InputTokens, int OutputTokens, string ModelId)
{
    /// <summary>The <see cref="Microsoft.Extensions.AI.UsageDetails.AdditionalCounts"/> key a provider translator reports prompt-cache writes under.</summary>
    public const string CacheWriteCountKey = "Thalos.CacheWriteInputTokens";

    /// <summary>Input tokens read from a prompt cache. Included in <see cref="InputTokens"/>, which stays the total.</summary>
    public int CacheReadTokens { get; init; }

    /// <summary>Input tokens written to a prompt cache. Included in <see cref="InputTokens"/>.</summary>
    public int CacheWriteTokens { get; init; }

    /// <summary>Zero usage for <paramref name="modelId"/> — the seed for a <c>+</c> accumulation.</summary>
    public static TurnUsage Empty(string modelId) => new(0, 0, modelId);

    /// <summary>
    /// Adds token counts. <c>ModelId</c> is taken from <paramref name="a"/> unless it is null/empty, in which case
    /// <paramref name="b"/>'s is used — so seeding with <c>Empty("")</c> and adding a real usage yields the real model id.
    /// </summary>
    public static TurnUsage operator +(TurnUsage a, TurnUsage b) =>
        new(a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens, string.IsNullOrEmpty(a.ModelId) ? b.ModelId : a.ModelId)
        { CacheReadTokens = a.CacheReadTokens + b.CacheReadTokens, CacheWriteTokens = a.CacheWriteTokens + b.CacheWriteTokens };
}
