using System.Globalization;

namespace Thalos.Mcp;

/// <summary>Temporary diagnostic trace, not for merge.</summary>
internal static class DiagTrace
{
    internal static System.Collections.Concurrent.ConcurrentQueue<string> Lines { get; } = new();

    internal static void Write(string line) =>
        Lines.Enqueue(DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + line);
}
