namespace Thalos.Mcp;

/// <summary>Temporary diagnostic trace, not for merge.</summary>
internal static class DiagTrace
{
    internal static System.Collections.Concurrent.ConcurrentQueue<string> Lines { get; } = new();

    internal static void Write(FormattableString line) =>
        Lines.Enqueue(FormattableString.Invariant($"{DateTime.UtcNow:HH:mm:ss.fff} {FormattableString.Invariant(line)}"));
}
