namespace Thalos;

/// <summary>
/// Token usage for one turn (summed over all model round-trips inside the turn).
/// Seed an accumulation with <see cref="Empty(string)"/> and fold with <c>+</c>.
/// Only input/output token counts are tracked in 0.1; prompt-cache read/write token counts are a follow-up.
/// </summary>
public readonly record struct TurnUsage(int InputTokens, int OutputTokens, string ModelId)
{
    /// <summary>Zero usage for <paramref name="modelId"/> — the seed for a <c>+</c> accumulation.</summary>
    public static TurnUsage Empty(string modelId) => new(0, 0, modelId);

    /// <summary>
    /// Adds token counts. <c>ModelId</c> is taken from <paramref name="a"/> unless it is null/empty, in which case
    /// <paramref name="b"/>'s is used — so seeding with <c>Empty("")</c> and adding a real usage yields the real model id.
    /// </summary>
    public static TurnUsage operator +(TurnUsage a, TurnUsage b) =>
        new(a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens, string.IsNullOrEmpty(a.ModelId) ? b.ModelId : a.ModelId);
}
