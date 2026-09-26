namespace Thalos.Tests.Mcp;

/// <summary>
/// Reads the test server's <c>--call-log</c> file while the server may still be appending to it. The server's append
/// holds the file open for writing, and <see cref="File.ReadAllText(string)"/> opens it allowing other readers only, so
/// on Windows that read fails with "being used by another process"; this read allows the server's write.
/// </summary>
internal static class CallLog
{
    /// <summary>The log's text, or empty when it does not exist yet.</summary>
    public static string Read(string path)
    {
        if (!File.Exists(path))
        {
            return "";
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The log's lines, or none when it does not exist yet.</summary>
    public static string[] Lines(string path) => Read(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
