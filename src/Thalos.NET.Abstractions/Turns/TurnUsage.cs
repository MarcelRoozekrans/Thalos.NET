namespace Thalos;

/// <summary>Token usage for one turn (summed over all model round-trips inside the turn).</summary>
public readonly record struct TurnUsage(int InputTokens, int OutputTokens, string ModelId)
{
    public static TurnUsage Empty(string modelId) => new(0, 0, modelId);

    public static TurnUsage operator +(TurnUsage a, TurnUsage b) =>
        new(a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens, a.ModelId);
}
